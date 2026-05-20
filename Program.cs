using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Steamworks;

namespace SteamWorkshopUploader;

internal static class Program
{
    static Program()
    {
        NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, asm, path) =>
        {
            if (!name.Equals("steam_api", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var baseDir = AppContext.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(baseDir, "libsteam_api.so"),
                         Path.Combine(baseDir, "runtimes", "linux-x64", "native", "libsteam_api.so"),
                     })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: SteamWorkshopUploader <workshop.json> \"<change note>\"");
            Console.Error.WriteLine();
            Console.Error.WriteLine("REMINDER: restart Steam (full Steam → Exit, wait 10s, relaunch) before");
            Console.Error.WriteLine("publishing. Otherwise the SteamUGC submit silently hangs at 'preparing");
            Console.Error.WriteLine("config' with no error. This is a documented Steam-side quirk.");
            return 2;
        }

        var jsonPath = Path.GetFullPath(args[0]);
        var changeNote = args[1];

        if (!File.Exists(jsonPath))
        {
            Console.Error.WriteLine($"workshop.json not found: {jsonPath}");
            return 2;
        }

        WorkshopManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WorkshopManifest>(
                File.ReadAllText(jsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("manifest deserialised to null");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"failed to parse {jsonPath}: {e.Message}");
            return 2;
        }

        if (!manifest.Validate(out var validationError))
        {
            Console.Error.WriteLine($"manifest invalid: {validationError}");
            return 2;
        }

        EWorkshopFileType fileType;
        if (!TryParseFileType(manifest.FileType, out fileType, out var fileTypeError))
        {
            Console.Error.WriteLine($"manifest invalid: {fileTypeError}");
            return 2;
        }

        var appId = new AppId_t(manifest.AppId);
        var language = string.IsNullOrWhiteSpace(manifest.Language) ? "English" : manifest.Language!;
        var contentFolder = ResolvePath(manifest.ContentFolder!, jsonPath);
        var previewFile = string.IsNullOrWhiteSpace(manifest.PreviewFile)
            ? null
            : ResolvePath(manifest.PreviewFile, jsonPath);

        if (!Directory.Exists(contentFolder))
        {
            Console.Error.WriteLine($"content folder does not exist: {contentFolder}");
            return 2;
        }

        long previewBytes = 0;
        if (previewFile != null)
        {
            if (!File.Exists(previewFile))
            {
                Console.Error.WriteLine($"preview file does not exist: {previewFile}");
                return 2;
            }
            previewBytes = new FileInfo(previewFile).Length;
            if (previewBytes >= 1_000_000)
            {
                Console.Error.WriteLine($"preview file too large: {previewBytes} bytes (Steam limit is 1 MB)");
                return 2;
            }
        }

        Console.WriteLine($"appid:    {manifest.AppId}");
        Console.WriteLine($"item:     {(string.IsNullOrWhiteSpace(manifest.PublishedFileId) ? "<will create new>" : manifest.PublishedFileId)}");
        Console.WriteLine($"title:    {manifest.Title}");
        Console.WriteLine($"tags:     [{string.Join(", ", manifest.Tags ?? new())}]");
        Console.WriteLine($"vis:      {(EVisibility)manifest.Visibility}");
        Console.WriteLine($"lang:     {language}");
        Console.WriteLine($"filetype: {fileType}");
        Console.WriteLine($"content:  {contentFolder}");
        Console.WriteLine(previewFile != null
            ? $"preview:  {previewFile} ({previewBytes} B)"
            : "preview:  <none — keeping existing>");
        Console.WriteLine($"note:     {changeNote}");
        Console.WriteLine();

        if (!SteamAPI.Init())
        {
            Console.Error.WriteLine("SteamAPI.Init() failed.");
            Console.Error.WriteLine("Check:");
            Console.Error.WriteLine("  - Steam is running and logged in to an account that owns this app");
            Console.Error.WriteLine($"  - steam_appid.txt sits next to the binary with content '{manifest.AppId}'");
            Console.Error.WriteLine("  - libsteam_api.so is in the binary's directory");
            return 1;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(manifest.PublishedFileId))
            {
                Console.WriteLine("no publishedfileid in manifest — creating a new Workshop item shell first...");
                Console.WriteLine($"  CreateItem fileType: {fileType}");
                if (!TryCreateItem(appId, fileType, out var newId, out var legalAgreementPending)) return 1;
                manifest.PublishedFileId = newId.ToString();
                PersistPublishedFileId(jsonPath, newId);
                Console.WriteLine($"created item {newId}. Persisted to manifest.");
                Console.WriteLine($"  https://steamcommunity.com/sharedfiles/filedetails/?id={newId}");
                if (legalAgreementPending)
                {
                    Console.WriteLine("\nNOTE: Workshop Legal Agreement not yet accepted.");
                    Console.WriteLine("If submit fails, visit the URL above and click 'I Agree' on the banner, then retry.");
                }
                Console.WriteLine();
            }
            return RunUpload(appId, manifest, contentFolder, previewFile, language, changeNote);
        }
        finally
        {
            SteamAPI.Shutdown();
        }
    }

    private static bool TryCreateItem(AppId_t appId, EWorkshopFileType fileType, out ulong publishedFileId, out bool legalAgreementPending)
    {
        publishedFileId = 0;
        legalAgreementPending = false;

        var apiCall = SteamUGC.CreateItem(appId, fileType);

        var done = false;
        var resultCode = EResult.k_EResultFail;
        ulong newId = 0;
        var legal = false;

        var callResult = CallResult<CreateItemResult_t>.Create((result, ioFailure) =>
        {
            if (ioFailure)
            {
                Console.Error.WriteLine("CreateItem: IO failure");
                resultCode = EResult.k_EResultIOFailure;
            }
            else
            {
                resultCode = result.m_eResult;
                newId = result.m_nPublishedFileId.m_PublishedFileId;
                legal = result.m_bUserNeedsToAcceptWorkshopLegalAgreement;
            }
            done = true;
        });
        callResult.Set(apiCall);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done && DateTime.UtcNow < deadline)
        {
            SteamAPI.RunCallbacks();
            Thread.Sleep(100);
        }

        if (!done)
        {
            Console.Error.WriteLine("CreateItem: no callback within 30s. Steam may be unreachable.");
            return false;
        }
        if (resultCode != EResult.k_EResultOK)
        {
            Console.Error.WriteLine($"CreateItem FAILED — EResult.{resultCode} ({(int)resultCode})");
            return false;
        }

        publishedFileId = newId;
        legalAgreementPending = legal;
        return true;
    }

    private static void PersistPublishedFileId(string jsonPath, ulong publishedFileId)
    {
        var text = File.ReadAllText(jsonPath);
        var doc = JsonNode.Parse(text)!.AsObject();
        doc["publishedfileid"] = publishedFileId.ToString();
        File.WriteAllText(jsonPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int RunUpload(AppId_t appId, WorkshopManifest manifest, string contentFolder, string? previewFile, string language, string changeNote)
    {
        var publishedFileId = new PublishedFileId_t(ulong.Parse(manifest.PublishedFileId!));

        var handle = SteamUGC.StartItemUpdate(appId, publishedFileId);

        if (!SteamUGC.SetItemContent(handle, contentFolder)) return Fail("SetItemContent");
        if (previewFile != null && !SteamUGC.SetItemPreview(handle, previewFile)) return Fail("SetItemPreview");
        if (!SteamUGC.SetItemUpdateLanguage(handle, language)) return Fail("SetItemUpdateLanguage");
        if (!SteamUGC.SetItemTitle(handle, manifest.Title!)) return Fail("SetItemTitle");
        if (!SteamUGC.SetItemDescription(handle, manifest.Description ?? "")) return Fail("SetItemDescription");
        if (!SteamUGC.SetItemVisibility(handle, (ERemoteStoragePublishedFileVisibility)manifest.Visibility)) return Fail("SetItemVisibility");
        if (manifest.Tags is { Count: > 0 } && !SteamUGC.SetItemTags(handle, manifest.Tags)) return Fail("SetItemTags");

        Console.WriteLine("submitting update...");
        var apiCall = SteamUGC.SubmitItemUpdate(handle, changeNote);

        var done = false;
        var resultCode = EResult.k_EResultFail;
        var userNeedsToAcceptWorkshopLegalAgreement = false;

        var callResult = CallResult<SubmitItemUpdateResult_t>.Create((result, ioFailure) =>
        {
            if (ioFailure)
            {
                Console.Error.WriteLine("SubmitItemUpdate: IO failure during call");
                resultCode = EResult.k_EResultIOFailure;
            }
            else
            {
                resultCode = result.m_eResult;
                userNeedsToAcceptWorkshopLegalAgreement = result.m_bUserNeedsToAcceptWorkshopLegalAgreement;
            }
            done = true;
        });
        callResult.Set(apiCall);

        var lastStatus = EItemUpdateStatus.k_EItemUpdateStatusInvalid;
        ulong lastProcessed = 0;
        var started = DateTime.UtcNow;
        var lastProgressAt = DateTime.UtcNow;

        while (!done)
        {
            SteamAPI.RunCallbacks();
            var status = SteamUGC.GetItemUpdateProgress(handle, out var processed, out var total);
            if (status != lastStatus || processed != lastProcessed)
            {
                lastStatus = status;
                lastProcessed = processed;
                lastProgressAt = DateTime.UtcNow;
                var pct = total > 0 ? $"{processed * 100 / total}%" : "—";
                var hr = total > 0 ? $"{HumanBytes(processed)}/{HumanBytes(total)}" : "";
                Console.WriteLine($"  [{(DateTime.UtcNow - started).TotalSeconds,5:F0}s] {StatusLabel(status),-22} {pct,5} {hr}");
            }
            else if (status == EItemUpdateStatus.k_EItemUpdateStatusPreparingConfig
                     && (DateTime.UtcNow - lastProgressAt).TotalSeconds > 60)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("⚠ Stuck at 'preparing config' for >60s with no progress.");
                Console.Error.WriteLine("  This is the well-known stale-Steam-state hang.");
                Console.Error.WriteLine("  Kill this process, fully restart Steam (Steam → Exit, wait 10s, relaunch),");
                Console.Error.WriteLine("  then retry. See README troubleshooting.");
                return 1;
            }
            Thread.Sleep(250);
        }

        if (resultCode == EResult.k_EResultOK)
        {
            Console.WriteLine($"\nSUCCESS — item {publishedFileId.m_PublishedFileId} updated.");
            Console.WriteLine($"  https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId.m_PublishedFileId}");
            if (userNeedsToAcceptWorkshopLegalAgreement)
            {
                Console.WriteLine("\nNOTE: Steam reports you still need to accept the Workshop Legal Agreement.");
                Console.WriteLine("Visit the item page above and click 'I Agree' on the banner, otherwise the item stays hidden.");
            }
            return 0;
        }

        Console.Error.WriteLine($"\nFAILED — EResult.{resultCode} ({(int)resultCode})");
        if (userNeedsToAcceptWorkshopLegalAgreement)
        {
            Console.Error.WriteLine("Steam reports you need to accept the Workshop Legal Agreement first.");
            Console.Error.WriteLine($"Visit https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId.m_PublishedFileId} and accept.");
        }
        return 1;
    }

    private static bool TryParseFileType(string? raw, out EWorkshopFileType fileType, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            fileType = EWorkshopFileType.k_EWorkshopFileTypeCommunity;
            error = "";
            return true;
        }
        var withPrefix = raw.StartsWith("k_EWorkshopFileType", StringComparison.Ordinal)
            ? raw
            : "k_EWorkshopFileType" + raw;
        if (Enum.TryParse<EWorkshopFileType>(withPrefix, true, out fileType))
        {
            error = "";
            return true;
        }
        fileType = default;
        error = $"filetype '{raw}' is not a recognised EWorkshopFileType (e.g. Community, Art, Microtransaction)";
        return false;
    }

    private static int Fail(string step)
    {
        Console.Error.WriteLine($"SteamUGC.{step} returned false");
        return 1;
    }

    private static string ResolvePath(string raw, string jsonPath)
    {
        var normalised = raw.Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalised)
            ? Path.GetFullPath(normalised)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(jsonPath)!, normalised));
    }

    private static string StatusLabel(EItemUpdateStatus s) => s switch
    {
        EItemUpdateStatus.k_EItemUpdateStatusInvalid              => "invalid",
        EItemUpdateStatus.k_EItemUpdateStatusPreparingConfig      => "preparing config",
        EItemUpdateStatus.k_EItemUpdateStatusPreparingContent     => "preparing content",
        EItemUpdateStatus.k_EItemUpdateStatusUploadingContent     => "uploading content",
        EItemUpdateStatus.k_EItemUpdateStatusUploadingPreviewFile => "uploading preview",
        EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges    => "committing",
        _ => s.ToString(),
    };

    private static string HumanBytes(ulong b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        if (b < 1024UL * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    private enum EVisibility { Public = 0, FriendsOnly = 1, Private = 2, Unlisted = 3 }
}

internal sealed class WorkshopManifest
{
    [JsonPropertyName("appid")]           public uint AppId { get; set; }
    [JsonPropertyName("publishedfileid")] public string? PublishedFileId { get; set; }
    [JsonPropertyName("contentfolder")]   public string? ContentFolder { get; set; }
    [JsonPropertyName("previewfile")]     public string? PreviewFile { get; set; }
    [JsonPropertyName("visibility")]      public int Visibility { get; set; }
    [JsonPropertyName("title")]           public string? Title { get; set; }
    [JsonPropertyName("description")]     public string? Description { get; set; }
    [JsonPropertyName("tags")]            public List<string>? Tags { get; set; }
    [JsonPropertyName("metadata")]        public string? Metadata { get; set; }
    [JsonPropertyName("language")]        public string? Language { get; set; }
    [JsonPropertyName("filetype")]        public string? FileType { get; set; }

    public bool Validate(out string error)
    {
        if (AppId == 0) { error = "appid is required and must be > 0 (your Steam App ID)"; return false; }
        if (!string.IsNullOrWhiteSpace(PublishedFileId) && !ulong.TryParse(PublishedFileId, out _))
        { error = "publishedfileid present but not a number"; return false; }
        if (string.IsNullOrWhiteSpace(ContentFolder)) { error = "contentfolder missing"; return false; }
        if (string.IsNullOrWhiteSpace(Title))         { error = "title missing"; return false; }
        if (Visibility is < 0 or > 3)                 { error = "visibility must be 0..3"; return false; }
        error = "";
        return true;
    }
}
