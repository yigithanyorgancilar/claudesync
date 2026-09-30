using System.Globalization;

namespace ClaudeSync;

/// <summary>
/// User-facing text in English and Turkish. The language is chosen once at startup
/// (Settings → Language, or the Windows display language); changing it needs a restart.
/// </summary>
public static class L
{
    public static bool Turkish { get; private set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Turkish ? "tr-TR" : "en-US");

    /// <param name="setting">"en", "tr" or null/"auto" for the Windows display language.</param>
    public static void Use(string? setting) => Turkish = setting switch
    {
        "tr" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr",
    };

    private static string T(string en, string tr) => Turkish ? tr : en;

    // ---- general -----------------------------------------------------------------------------------------
    public static string Tagline => T("Your Claude Code conversations on every computer", "Claude Code sohbetlerin bütün bilgisayarlarında");
    public static string SyncNow => T("Sync now", "Şimdi eşitle");
    public static string Syncing => T("Syncing…", "Eşitleniyor…");
    public static string SyncAllTooltip => T("Check all conversations now", "Tüm sohbetleri şimdi kontrol et");
    public static string AddConversation => T("Add conversation", "Sohbet ekle");
    public static string Account => T("Account", "Hesap");
    public static string GoogleAccount => T("Google account", "Google hesabı");
    public static string SearchConversations => T("Search conversations", "Sohbet ara");
    public static string SectionLinked => T("Synced on this computer", "Bu cihazda eşitlenenler");
    public static string SectionCloud => T("In the cloud, not on this computer", "Bulutta, bu cihazda değil");
    public static string Untitled => T("Untitled conversation", "Adsız sohbet");
    public static string UnknownConversation => T("Unknown conversation", "Bilinmeyen sohbet");
    public static string Project => T("Project", "Proje");
    public static string Conversation => T("Conversation", "Sohbet");
    public static string Cancel => T("Cancel", "Vazgeç");
    public static string Ok => T("OK", "Tamam");
    public static string Close => T("Close", "Kapat");
    public static string Save => T("Save", "Kaydet");
    public static string Log => T("Log", "Günlük");
    public static string LogTooltip => T("Log and data folder", "Günlük ve veri klasörü");
    public static string Settings => T("Settings", "Ayarlar");
    public static string Support => T("Support", "Destek ol");
    public static string SupportTooltip => T("ClaudeSync is free and open source. If it saves you time, you can buy me a coffee.",
        "ClaudeSync ücretsiz ve açık kaynak. İşine yaradıysa bir kahve ısmarlayabilirsin.");

    // ---- empty states ------------------------------------------------------------------------------------
    public static string SelectConversation => T("Select a conversation", "Bir sohbet seç");
    public static string SelectConversationHint => T("Pick a conversation on the left to see its status and options.",
        "Soldaki listeden bir sohbet seçerek durumunu ve seçeneklerini görebilirsin.");
    public static string NoConversations => T("No conversations yet", "Henüz sohbet eklenmedi");
    public static string NoConversationsHint => T("Add a Claude Code conversation from this computer. It shows up on your other computer once you sign in there with the same account.",
        "Bu bilgisayardaki bir Claude Code sohbetini ekle. Diğer bilgisayarında aynı hesapla giriş yapınca orada da görünür.");

    // ---- detail pane -------------------------------------------------------------------------------------
    public static string TakeToThisComputer => T("Get on this computer", "Bu cihaza al");
    public static string CloseClaudeAndApply => T("Close Claude and apply", "Claude'u kapat ve uygula");
    public static string KeepThisComputer => T("Keep this computer's version", "Bu bilgisayardakini koru");
    public static string KeepThisComputerTooltip => T("This computer's version is uploaded; the cloud version stays in Backups.",
        "Bu bilgisayardaki hali buluta yüklenir; buluttaki hali Yedekler'de kalır.");
    public static string TakeCloud => T("Use the cloud version", "Buluttakini al");
    public static string TakeCloudTooltip => T("The cloud version is applied here; this computer's version is backed up first.",
        "Buluttaki hali bu bilgisayara uygulanır; buradaki hali önce yedeklenir.");
    public static string ChooseFolder => T("Choose folder", "Klasör seç");
    public static string LastSyncLabel => T("Last sync", "Son eşitleme");
    public static string FolderLabel => T("Folder on this computer", "Bu bilgisayardaki klasör");
    public static string NotChosen => T("Not chosen", "Seçilmedi");
    public static string NeverSynced => T("Not synced yet", "Henüz eşitlenmedi");
    public static string Backups => T("Backups", "Yedekler");
    public static string OpenFolder => T("Open folder", "Klasörü aç");
    public static string RemoveFromSync => T("Remove from sync", "Eşitlemeden kaldır");
    public static string RemoveFromSyncTooltip => T("Stops syncing this conversation on all computers and deletes its cloud copy. The conversation on each computer is kept.",
        "Sohbeti bütün bilgisayarlarda eşitlemeden çıkarır ve buluttaki kopyasını siler. Bilgisayarlardaki sohbet olduğu gibi kalır.");

    // ---- statuses ----------------------------------------------------------------------------------------
    public static string InCloud => T("In the cloud", "Bulutta");
    public static string NotOnThisComputer => T("Not on this computer", "Bu cihaza alınmamış");
    public static string NotOnThisComputerDetail => T("This conversation was added from another computer. To continue it here, choose “Get on this computer” and pick the project's folder on this computer.",
        "Bu sohbet başka bir bilgisayardan eklendi. Burada devam etmek için “Bu cihaza al” ile projenin bu bilgisayardaki klasörünü seç.");
    public static string Waiting => T("Waiting for first check", "Kontrol bekleniyor");
    public static string WaitingDetail => T("The first check runs shortly.", "İlk kontrol birazdan yapılacak.");
    public static string UpToDate => T("Up to date", "Güncel");
    public static string UpToDateDetail => T("This computer and the cloud have the same version.", "Bu bilgisayar ile buluttaki son hali aynı.");
    public static string Uploaded => T("Uploaded", "Buluta yüklendi");
    public static string UploadedDetail => T("The latest messages were uploaded. Your other computer picks them up at its next check.",
        "Son mesajlar buluta gönderildi. Diğer bilgisayar bir sonraki kontrolde alacak.");
    public static string Downloaded => T("Downloaded", "Bu cihaza indirildi");
    public static string DownloadedDetail => T("The latest version from your other computer was applied here. Open the conversation in Claude Desktop to continue.",
        "Diğer bilgisayardaki son hali buraya uygulandı. Claude Desktop'ta sohbeti açıp devam edebilirsin.");
    public static string PendingShort => T("New messages waiting", "Yeni mesajlar bekliyor");
    public static string PendingTitle => T("New messages from your other computer", "Diğer bilgisayardan yeni mesajlar var");
    public static string NewEntries(int count) => T($"{count} new entries downloaded. ", $"{count} yeni kayıt indirildi. ");
    public static string InProgressShort => T("In progress", "Sohbet sürüyor");
    public static string InProgressTitle => T("Conversation in progress", "Sohbet devam ediyor");
    public static string ConflictShort => T("Conflict", "Çakışma");
    public static string ConflictTitle => T("Continued differently on two computers", "İki bilgisayarda farklı devam etmiş");
    public static string ConflictDetail(int local, int remote) => T(
        $"{local} entries here and {remote} in the cloud are missing on the other side. Choose which one to continue with — the other one is not deleted, it stays in Backups.",
        $"Bu bilgisayarda {local}, bulutta {remote} kayıt diğer tarafta yok. Hangisiyle devam edeceğini seç — diğeri silinmez, Yedekler'de kalır.");
    public static string NeedsFolderShort => T("Choose a folder", "Klasör seçilmeli");
    public static string NeedsFolderTitle => T("Project folder needed", "Proje klasörü gerekli");
    public static string NotFoundShort => T("Not found", "Bulunamadı");
    public static string NotFoundTitle => T("Not found on this computer", "Bu bilgisayarda bulunamadı");
    public static string NotInCloudShort => T("Not in the cloud", "Bulutta yok");
    public static string NotInCloudTitle => T("Not found in the cloud", "Bulutta bulunamadı");
    public static string RetryShort => T("Will retry", "Tekrar denenecek");
    public static string ErrorShort => T("Error", "Hata");
    public static string ErrorTitle => T("Could not sync", "Eşitlenemedi");

    // ---- banner / footer ---------------------------------------------------------------------------------
    public static string CloudUnreachable => T("Cannot reach the cloud", "Buluta bağlanılamadı");
    public static string SyncFailed => T("Sync failed", "Eşitleme yapılamadı");
    public static string TryAgain => T("Try again", "Tekrar dene");
    public static string ContinuedElsewhere(string title) => T($"“{title}” continued on your other computer", $"“{title}” diğer bilgisayarda devam etmiş");
    public static string NewMessagesIn(int count) => T($"New messages from your other computer in {count} conversations", $"{count} sohbette diğer bilgisayardan yeni mesajlar var");
    public static string DesktopOpenBanner => T("Conversation files are not touched while Claude Desktop is open; they are applied automatically when you close it. Or apply now.",
        "Claude Desktop açıkken sohbet dosyalarına dokunulmaz; kapattığında otomatik uygulanır. İstersen şimdi uygula.");
    public static string PendingApply => T("New messages waiting to be applied", "Yeni mesajlar uygulanmayı bekliyor");
    public static string ApplyNow => T("Apply now", "Şimdi uygula");
    public static string ConflictBanner(string title) => T($"“{title}” continued differently on two computers", $"“{title}” iki bilgisayarda farklı devam etmiş");
    public static string ConflictBannerMany(int count) => T($"{count} conversations continued differently on two computers", $"{count} sohbet iki bilgisayarda farklı devam etmiş");
    public static string ConflictBannerDetail => T("Nothing was deleted. Open the conversation and choose which version to continue with.",
        "Hiçbir şey silinmedi. Sohbeti açıp hangisiyle devam edeceğini seç.");
    public static string Show => T("Show", "Göster");
    public static string FailedOne(string title) => T($"“{title}” could not be synced", $"“{title}” eşitlenemedi");
    public static string FailedMany(int count) => T($"{count} conversations could not be synced", $"{count} sohbet eşitlenemedi");
    public static string DesktopOpen => T("Claude Desktop open", "Claude Desktop açık");
    public static string DesktopClosed => T("Claude Desktop closed", "Claude Desktop kapalı");
    public static string FooterSignedOut => T("Sign in to sync", "Eşitleme için giriş yapılmalı");
    public static string FooterChecking => T("Checking conversations…", "Sohbetler kontrol ediliyor…");
    public static string FooterLastCheck(string when) => T($"Last check {when} · automatic every minute", $"Son kontrol {when} · her dakika otomatik");
    public static string FooterAuto => T("Automatic sync on", "Otomatik eşitleme açık");
    public static string TrayAttention => T("ClaudeSync · a conversation needs attention", "ClaudeSync · dikkat gerektiren sohbet var");
    public static string ClosingDesktop => T("Closing Claude Desktop…", "Claude Desktop kapatılıyor…");

    // ---- tray --------------------------------------------------------------------------------------------
    public static string TrayOpen => T("Open ClaudeSync", "ClaudeSync'i aç");
    public static string TrayCloseAndSync => T("Close Claude and sync", "Claude'u kapat ve eşitle");
    public static string TrayExit => T("Exit", "Çıkış");
    public static string TrayRunningTitle => T("ClaudeSync keeps running in the background", "ClaudeSync arka planda çalışıyor");
    public static string TrayRunningText => T("Conversations keep syncing. To quit, right-click the tray icon and choose Exit.",
        "Sohbetler eşitlenmeye devam ediyor. Kapatmak için tepsi simgesine sağ tıklayıp Çıkış'ı seç.");
    public static string NotifyUpdated(string title) => T($"“{title}” updated", $"“{title}” güncellendi");
    public static string NotifyUpdatedText => T("The latest messages from your other computer were applied here. Open it in Claude Desktop to continue.",
        "Diğer bilgisayardaki son mesajlar buraya uygulandı. Claude Desktop'ta açıp devam edebilirsin.");
    public static string NotifyPending => T("New messages from your other computer", "Diğer bilgisayardan yeni mesajlar var");
    public static string NotifyPendingText(string title) => T($"New messages for “{title}” are ready. They are applied when Claude Desktop closes.",
        $"“{title}” için yeni mesajlar hazır. Claude Desktop kapanınca uygulanacak.");
    public static string NotifyConflict => T("Conversation continued differently on two computers", "Sohbet iki bilgisayarda farklı devam etmiş");
    public static string NotifyConflictText(string title) => T($"“{title}”: choose which version to keep in ClaudeSync.", $"“{title}”: hangisiyle devam edeceğini ClaudeSync'te seç.");
    public static string NotifyRemoved(string title) => T($"“{title}” removed from sync", $"“{title}” eşitlemeden kaldırıldı");
    public static string NotifyRemovedText => T("It was removed on another computer. The conversation on this computer is kept.",
        "Başka bir bilgisayarda kaldırıldı. Bu bilgisayardaki sohbet olduğu gibi duruyor.");

    // ---- actions & dialogs -------------------------------------------------------------------------------
    public static string CannotReadClaude => T("Could not read Claude conversations", "Claude sohbetleri okunamadı");
    public static string AddFailed => T("Could not add the conversation", "Sohbet eklenemedi");
    public static string JoinTitle => T("Get on this computer", "Bu cihaza al");
    public static string JoinText(string title, string folder) => T($"“{title}” will be connected to this project folder on this computer:\n\n{folder}",
        $"“{title}” bu bilgisayarda şu proje klasörüne bağlanacak:\n\n{folder}");
    public static string OtherFolder => T("Choose another folder…", "Başka klasör seç…");
    public static string PickConversationFolder(string title) => T($"Choose the project folder of “{title}” on this computer", $"“{title}” sohbetinin bu bilgisayardaki proje klasörünü seç");
    public static string PickProjectFolder(string project) => T($"Choose the folder of project “{project}” on this computer", $"“{project}” projesinin bu bilgisayardaki klasörünü seç");
    public static string JoinFailed => T("Could not get the conversation on this computer", "Sohbet bu cihaza alınamadı");
    public static string FolderSaveFailed => T("Could not save the folder", "Klasör kaydedilemedi");
    public static string KeepLocalTitle => T("Keep this computer's version?", "Bu bilgisayardaki hali korunsun mu?");
    public static string KeepLocalText => T("This computer's conversation is uploaded as a new version and your other computer gets it too. The cloud version is not deleted; you can restore it from Backups.",
        "Bu bilgisayardaki sohbet buluta yeni sürüm olarak yüklenecek ve diğer bilgisayar da bunu alacak. Buluttaki hali silinmez; Yedekler'den geri alınabilir.");
    public static string ConflictFailed => T("Could not resolve the conflict", "Çakışma çözülemedi");
    public static string TakeCloudTitle => T("Use the cloud version?", "Buluttaki hal alınsın mı?");
    public static string TakeCloudText => T("The cloud version is applied to this computer. This computer's version is backed up first; you can restore it from Backups.",
        "Buluttaki sohbet bu bilgisayara uygulanacak. Buradaki hali önce otomatik yedeklenir; Yedekler'den geri alınabilir.");
    public static string TakeCloudRestart => T("\n\nClaude Desktop will be closed briefly and reopened for this.", "\n\nBunun için Claude Desktop kısa süreliğine kapatılıp yeniden açılacak.");
    public static string CloseClaudeTitle => T("Close Claude Desktop?", "Claude Desktop kapatılsın mı?");
    public static string CloseClaudeText => T("To apply the messages from your other computer, Claude Desktop is closed and reopened after syncing.\n\nIf Claude is writing a reply, wait for it to finish; an unfinished reply may be lost. Claude Code sessions in a terminal are not touched.",
        "Diğer bilgisayardan gelen mesajları uygulamak için Claude Desktop kapatılıp eşitlemeden sonra yeniden açılacak.\n\nClaude şu an bir yanıt yazıyorsa önce bitmesini bekle; yarım kalan yanıt kaybolabilir. Terminalde açık Claude Code oturumlarına dokunulmaz.");
    public static string CloseAndApply => T("Close and apply", "Kapat ve uygula");
    public static string CloseClaudeFailed => T("Could not close Claude Desktop", "Claude Desktop kapatılamadı");
    public static string ReopenFailedTitle => T("Could not open Claude Desktop", "Claude Desktop açılamadı");
    public static string ReopenFailedText => T("Done, but Claude Desktop could not be reopened automatically; please open it yourself.\n\n",
        "İşlem tamamlandı ama Claude Desktop otomatik açılamadı; kendin açabilirsin.\n\n");
    public static string RemoveTitle => T("Remove from sync?", "Eşitlemeden kaldırılsın mı?");
    public static string RemoveText(string title) => T(
        $"“{title}” stops syncing on all computers and its cloud copy (all versions) is deleted.\n\nThe Claude conversation on each computer is not deleted; it stays as a normal conversation. You can add it again later.",
        $"“{title}” bütün bilgisayarlarda eşitlemeden çıkarılacak ve buluttaki kopyası (tüm sürümleriyle) silinecek.\n\nBilgisayarlardaki Claude sohbeti silinmez; her iki bilgisayarda da normal bir sohbet olarak kalır. İstersen sonra yeniden ekleyebilirsin.");
    public static string RemoveFailed => T("Could not remove the conversation", "Sohbet kaldırılamadı");
    public static string SignInWithGoogle => T("Sign in with Google", "Google ile giriş yap");
    public static string WaitingForBrowser => T("Waiting for the browser…", "Tarayıcıda giriş bekleniyor…");
    public static string SignInFailedTitle => T("Could not sign in", "Giriş yapılamadı");
    public static string ThisComputer(string name) => T("This computer: " + name, "Bu bilgisayar: " + name);
    public static string SwitchAccount => T("Switch account", "Hesap değiştir");
    public static string SignOut => T("Sign out", "Oturumu kapat");
    public static string SignOutTitle => T("Sign out?", "Oturum kapatılsın mı?");
    public static string SignOutText => T("Syncing stops. Your conversations and settings stay on this computer; sign in with the same account to continue where you left off.",
        "Eşitleme durur. Seçtiğin sohbetler ve ayarlar bu bilgisayarda kalır; aynı hesapla tekrar giriş yapınca kaldığı yerden devam eder.");
    public static string UnexpectedError => T("Unexpected error", "Beklenmeyen hata");

    // ---- welcome / server setup --------------------------------------------------------------------------
    public static string Welcome => T("Welcome to ClaudeSync", "ClaudeSync'e hoş geldin");
    public static string WelcomeText => T("Move Claude Desktop's Code conversations between your computers: continue on one exactly where you left off on the other.",
        "Claude Desktop'taki Code sohbetlerini bilgisayarların arasında taşı: birinde bıraktığın sohbete diğerinde kaldığın yerden devam et.");
    public static string SignInHint => T("Your browser opens the Google sign-in page. Sign in with the same account on your other computer.",
        "Tarayıcında Google giriş sayfası açılır. Diğer bilgisayarda da aynı hesapla giriş yap.");
    public static string ServerTitle => T("Connect your sync server", "Eşitleme sunucunu bağla");
    public static string ServerText => T("ClaudeSync keeps your conversations on your own free Supabase + Cloudflare setup, so nobody else holds your data. Set it up once (about 15 minutes), then paste the connection code — or the three values — here.",
        "ClaudeSync sohbetlerini kendi ücretsiz Supabase + Cloudflare kurulumunda tutar; verini başka kimse tutmaz. Bir kez kur (yaklaşık 15 dakika), sonra bağlantı kodunu — ya da üç değeri — buraya yapıştır.");
    public static string SetupGuide => T("Open the setup guide", "Kurulum rehberini aç");
    public static string ConnectionCode => T("Connection code", "Bağlantı kodu");
    public static string ConnectionCodeHint => T("From Settings on a computer that is already connected", "Zaten bağlı bir bilgisayarın Ayarlar ekranından");
    public static string OrEnterValues => T("or enter the values", "ya da değerleri gir");
    public static string SupabaseUrl => T("Supabase URL", "Supabase adresi");
    public static string PublishableKey => T("Supabase publishable key", "Supabase publishable key");
    public static string WorkerUrl => T("Worker API URL", "Worker API adresi");
    public static string Connect => T("Connect", "Bağlan");
    public static string InvalidServerTitle => T("Invalid server settings", "Sunucu ayarları geçersiz");
    public static string InvalidServerText => T("Both addresses must start with https:// and the publishable key must not be empty.",
        "Sunucu adresleri https:// ile başlamalı ve publishable key boş olmamalı.");
    public static string InvalidCode => T("This connection code is not valid.", "Bu bağlantı kodu geçerli değil.");
    public static string ServerNotConfigured => T("The sync server is not set up yet.", "Eşitleme sunucusu henüz ayarlanmadı.");

    // ---- add conversation window -------------------------------------------------------------------------
    public static string AddText => T("Choose the Claude Code conversations on this computer you want to sync. They are uploaded and appear on your other computer.",
        "Bu bilgisayardaki Claude Code sohbetlerinden eşitlemek istediklerini seç. Seçtiklerin buluta yüklenir ve diğer bilgisayarında görünür.");
    public static string SearchTitleOrProject => T("Search title or project", "Başlık veya proje ara");
    public static string NothingToAdd => T("No conversations to add. Conversations you start in Claude Desktop's Code tab appear here; ones already added are not listed.",
        "Eklenebilecek sohbet yok. Claude Desktop'ta Code sekmesinde başlattığın sohbetler burada görünür; zaten eklenmiş olanlar listelenmez.");
    public static string Archived => T("archived", "arşivde");
    public static string ConversationCount(int count) => T(count == 1 ? "1 conversation" : $"{count} conversations", $"{count} sohbet");
    public static string SelectedCount(int count) => T(count == 1 ? "1 conversation selected" : $"{count} conversations selected", $"{count} sohbet seçildi");
    public static string Add => T("Add", "Ekle");
    public static string AddCount(int count) => T($"Add {count} conversations", $"{count} sohbeti ekle");

    // ---- backups window ----------------------------------------------------------------------------------
    public static string BackupsText => T("The version you restore is applied to this computer and uploaded as a new version. The current version is backed up first.",
        "Geri yüklediğin hal bu bilgisayara uygulanır ve buluta yeni sürüm olarak gönderilir. Uygulamadan önce mevcut hal otomatik yedeklenir.");
    public static string CurrentInCloud => T("current in the cloud", "buluttaki güncel hali");
    public static string Restore => T("Restore", "Geri yükle");
    public static string RestoreNeedsJoin => T("Get the conversation on this computer to restore backups.", "Geri yüklemek için önce sohbeti bu cihaza al.");
    public static string LoadingBackups => T("Loading backups…", "Yedekler yükleniyor…");
    public static string NoBackups => T("No backups for this conversation yet.", "Bu sohbet için henüz yedek yok.");
    public static string BackupsFailed => T("Could not load backups: ", "Yedekler yüklenemedi: ");
    public static string UnknownDate => T("Unknown date", "Tarih bilinmiyor");
    public static string RestoreTitle => T("Restore this backup?", "Bu yedek geri yüklensin mi?");
    public static string RestoreText(string title, string when) => T(
        $"“{title}” is restored to its state from {when} and that state is sent to your other computers too. The current state is backed up first.",
        $"“{title}” sohbeti {when} tarihli haline döndürülecek ve bu hal diğer bilgisayarlara da gönderilecek. Şu anki hali önce yedeklenir.");
    public static string Restoring => T("Restoring…", "Geri yükleniyor…");
    public static string Restored => T("Restored and uploaded.", "Geri yüklendi ve buluta gönderildi.");
    public static string RestoredLocally => T("Restored on this computer. ", "Bu bilgisayara geri yüklendi. ");
    public static string RestoreFailed => T("Could not restore", "Geri yüklenemedi");
    public static string CloudVersion => T("Cloud version", "Bulut sürümü");
    public static string ConflictLocalCopy => T("Conflict · this computer's copy", "Çakışma · bu cihazdaki kopya");
    public static string ConflictCloudCopy => T("Conflict · cloud copy", "Çakışma · buluttaki kopya");
    public static string AutoBackup => T("Automatic backup · before it was replaced", "Otomatik yedek · değiştirilmeden önceki hali");

    // ---- settings window ---------------------------------------------------------------------------------
    public static string General => T("General", "Genel");
    public static string StartWithWindows => T("Start ClaudeSync in the background when Windows starts", "Windows açılınca ClaudeSync'i arka planda başlat");
    public static string StartWithWindowsHint => T("Recommended: when the computer starts, messages from your other computer are downloaded before you open Claude Desktop.",
        "Önerilir: bilgisayar açılınca, Claude Desktop'ı açmadan önce diğer bilgisayardaki son mesajlar indirilir.");
    public static string Language => T("Language", "Dil");
    public static string LanguageAuto => T("Windows language", "Windows dili");
    public static string LanguageRestart => T("Takes effect after restarting ClaudeSync.", "ClaudeSync yeniden başlatılınca geçerli olur.");
    public static string DetectedClaude(string group, string projects) => T($"Claude data found.\nSession records: {group}\nClaude Code conversations: {projects}",
        $"Claude verisi bulundu.\nOturum kayıtları: {group}\nClaude Code sohbetleri: {projects}");
    public static string ClaudeNotDetected => T("Claude Desktop data could not be found automatically: ", "Claude Desktop verisi otomatik bulunamadı: ");
    public static string ManualFolders => T("Set folders manually", "Klasörleri elle ayarla");
    public static string ManualFoldersHint => T("Only fill these in if the wrong folder is detected. Empty fields are detected automatically.",
        "Yalnızca otomatik bulma yanlış klasörü seçerse doldur. Boş bırakılan alanlar otomatik bulunur.");
    public static string DesktopDataFolder => T("Claude Desktop data folder", "Claude Desktop veri klasörü");
    public static string AccountFolder => T("Account folder name (under claude-code-sessions)", "Hesap klasörü adı (claude-code-sessions altındaki)");
    public static string WorkspaceFolder => T("Workspace folder name", "Çalışma alanı klasörü adı");
    public static string ProjectsFolder => T("Claude Code projects folder", "Claude Code projeler klasörü");
    public static string Server => T("Sync server", "Eşitleme sunucusu");
    public static string ServerAddresses => T("Server addresses", "Sunucu adresleri");
    public static string CopyConnectionCode => T("Copy connection code", "Bağlantı kodunu kopyala");
    public static string CopyConnectionCodeHint => T("Paste it on your other computer to connect it to the same server.", "Diğer bilgisayarını aynı sunucuya bağlamak için orada yapıştır.");
    public static string Copied => T("Copied", "Kopyalandı");
    public static string ThisComputerSection => T("This computer", "Bu bilgisayar");
    public static string DeviceId(string id) => T("Device id: " + id, "Cihaz kimliği: " + id);
    public static string NotRegistered => T("Not registered yet", "Henüz kayıtlı değil");
    public static string OpenDataFolder => T("Open data folder", "Veri klasörünü aç");
    public static string FoldersIncomplete => T("Folder settings incomplete", "Klasör ayarları eksik");
    public static string FoldersIncompleteText => T("Fill in both the account and workspace folders, or leave both empty.",
        "Hesap ve çalışma alanı klasörleri birlikte doldurulmalı ya da ikisi de boş bırakılmalı.");
    public static string About => T("About", "Hakkında");
    public static string AboutText => T("Free and open source (MIT). Not affiliated with Anthropic.", "Ücretsiz ve açık kaynak (MIT). Anthropic ile bağlantılı değildir.");
    public static string SourceCode => T("Source code", "Kaynak kod");
    public static string BuyMeACoffee => T("Buy me a coffee", "Bir kahve ısmarla");

    // ---- engine & cloud messages -------------------------------------------------------------------------
    public static string SavedSignInUnreadable => T("The saved sign-in could not be read. Please sign in again.", "Kayıtlı giriş bilgisi okunamadı. Tekrar giriş yap.");
    public static string DesktopStayedOpen => T("Claude Desktop stayed open.", "Claude Desktop açık kaldı.");
    public static string DesktopNotClosed => T("Claude Desktop could not be closed.", "Claude Desktop kapatılamadı.");
    public static string EmptyResponse => T("The server sent an empty response.", "Sunucudan boş yanıt geldi.");
    public static string SignInNotCompleted => T("Google sign-in was not completed.", "Google girişi tamamlanmadı.");
    public static string SignInTimeout => T("Google sign-in was not completed within 5 minutes.", "Google girişi 5 dakika içinde tamamlanmadı.");
    public static string BrowserFailedTitle => T("Sign-in not completed", "Giriş tamamlanmadı");
    public static string BrowserFailedText => T("Go back to ClaudeSync and try again.", "ClaudeSync'e dönüp tekrar deneyebilirsin.");
    public static string BrowserDoneTitle => T("Signed in", "Giriş tamamlandı");
    public static string BrowserDoneText => T("You can close this tab and return to ClaudeSync.", "Bu sekmeyi kapatıp ClaudeSync'e dönebilirsin.");
    public static string SessionExpired => T("Your session has expired. Please sign in again.", "Oturumun süresi dolmuş. Tekrar giriş yap.");
    public static string SignInFailed => T("Sign-in failed: ", "Giriş başarısız: ");
    public static string SignInFirst => T("Sign in with Google first.", "Önce Google ile giriş yap.");
    public static string UserInfoFailed => T("Could not get the account details.", "Kullanıcı bilgisi alınamadı.");
    public static string ServerUnreachable => T("Cannot reach the server: ", "Sunucuya ulaşılamadı: ");
    public static string ServerTimeout => T("The server did not respond in time.", "Sunucu zamanında yanıt vermedi.");
    public static string NoVersionId => T("The server did not return a version id.", "Sunucu sürüm numarası döndürmedi.");
    public static string ServerNoDelete => T("The server does not support removing conversations yet. Deploy the latest Worker (server/worker.js).",
        "Sunucu sohbet silmeyi henüz desteklemiyor. Güncel Worker'ı (server/worker.js) yayınla.");
    public static string DesktopDataNotFound => T("Claude Desktop's data folder was not found. You can choose it in Settings.", "Claude Desktop veri klasörü bulunamadı. Ayarlar'dan elle seçebilirsin.");
    public static string AccountFolderNotFound => T("The Claude account folder from Settings was not found.", "Ayarlardaki Claude hesap klasörü bulunamadı.");
    public static string NoCodeConversations => T("Claude Desktop has no Code conversations yet.", "Claude Desktop'ta henüz Code sohbeti yok.");
    public static string InvalidPackage => T("Invalid cloud package.", "Geçersiz bulut paketi.");
    public static string PackageTooLarge => T("Cloud package is too large.", "Bulut paketi çok büyük.");
    public static string EmptyManifest => T("Package manifest is empty.", "Paket bildirimi boş.");
    public static string PackageNotVerified => T("Cloud package could not be verified.", "Bulut paketi doğrulanamadı.");
    public static string EmptySession => T("Session metadata is empty.", "Oturum bilgisi boş.");
    public static string PackageOtherConversation => T("The package belongs to another conversation.", "Paket başka bir sohbete ait.");
    public static string RetryConcurrent => T("Another computer uploaded at the same time; retrying at the next check.", "Başka bir cihaz aynı anda yükleme yaptı; bir sonraki kontrolde yeniden denenecek.");
    public static string NotSyncedHere => T("This conversation is not synced on this computer.", "Bu sohbet bu cihazda eşitlenmiyor.");
    public static string NoFolderSelected => T("No project folder chosen on this computer.", "Bu cihazda proje klasörü seçilmemiş.");
    public static string FolderMissing(string path) => T("Project folder not found: " + path, "Proje klasörü bulunamadı: " + path);
    public static string RemovedFromCloud => T("Removed from the cloud; syncing stopped on this computer too.", "Sohbet buluttan kaldırılmış; bu bilgisayarda da eşitleme durduruldu.");
    public static string NeverUploaded => T("Never uploaded and not found on this computer.", "Sohbet buluta hiç yüklenmemiş ve bu cihazda da bulunamadı.");
    public static string ConflictMessage => T("The conversation continued separately on two computers. Choose which one to keep; the other stays in Backups.",
        "Sohbet iki cihazda ayrı ayrı devam etmiş. Hangisinin korunacağını seç; diğeri yedeklerde kalır.");
    public static string BlockedDesktop => T("Claude Desktop is open. It is applied automatically when you close it.", "Claude Desktop açık. Kapattığında otomatik uygulanacak.");
    public static string BlockedTerminal => T("The conversation is open in a Claude Code terminal. It is applied when you close that session.",
        "Sohbet bir Claude Code terminalinde açık. Terminal oturumunu kapatınca uygulanacak.");
    public static string BlockedMaybeTerminal => T("An open Claude Code terminal may be using this conversation. It is applied when you close the terminal.",
        "Açık bir Claude Code terminali bu sohbeti kullanıyor olabilir. Terminali kapatınca uygulanacak.");
    public static string Typing => T("The conversation is being written; it is uploaded after a short quiet period.", "Sohbet şu an yazılıyor; kısa bir süre boşta kalınca buluta gönderilecek.");
    public static string PackageTooBig(long mb) => T($"The conversation package is {mb} MB; the server limit is 100 MB.", $"Sohbet paketi {mb} MB; sunucu sınırı 100 MB.");
    public static string VersionNotFound => T("Cloud version not found.", "Bulut sürümü bulunamadı.");
    public static string DownloadNotVerified => T("The downloaded cloud package could not be verified.", "İndirilen bulut paketi doğrulanamadı.");
    public static string OtherConversationHere => T("Another conversation on this computer uses the same id.", "Bu cihazda aynı kimliği kullanan başka bir sohbet var.");
    public static string RestoreNeedsSync => T("The conversation must be synced on this computer to restore it.", "Geri yüklemek için sohbetin bu cihazda eşitleniyor olması gerekir.");
    public static string RestoreNeedsClosed => T("Claude must be closed to restore. ", "Geri yükleme için Claude kapalı olmalı. ");
    public static string UnknownBackup => T("Unknown backup type.", "Bilinmeyen yedek türü.");
    public static string BackupOtherConversation => T("This backup belongs to another conversation.", "Yedek bu sohbete ait değil.");
    public static string BackupNoCurrent => T("The backup has no current transcript.", "Yedekte sohbetin güncel transcript'i yok.");

    // ---- time --------------------------------------------------------------------------------------------
    public static string JustNow => T("just now", "az önce");
    public static string MinutesAgo(int minutes) => T($"{minutes} min ago", $"{minutes} dk önce");
    public static string Today(string time) => T("today " + time, "bugün " + time);
    public static string Yesterday(string time) => T("yesterday " + time, "dün " + time);
}
