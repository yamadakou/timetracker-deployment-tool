using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace Timetracker.SystemTests.E2E;

/// <summary>セットアップウィザードで作る管理ユーザー（セットアップ後のログインにも使う）。</summary>
public sealed record AdminUser(string LoginName, string DisplayName, string Password);

/// <summary>
/// Timetracker のセットアップウィザード（/setup）とログインを、ブラウザー（Playwright）で操作する。
/// 画面は ExtJS で作られていて要素の ID が自動生成されるため、表示されている文言とラベルで要素を探す。
/// ウィザードの流れ: セットアップの開始 → ライセンスキーの入力 → ライセンス情報の確認 → データベースの作成方法
/// → 管理ユーザーの登録 → 実行の確認 → 実行中 → セットアップの完了（または セットアップエラー）。
/// </summary>
public sealed class TimetrackerSetupWizard
{
    private readonly IPage _page;

    public TimetrackerSetupWizard(IPage page)
    {
        _page = page;
    }

    /// <summary>ヘッドレスの Chromium を起動する。TT_E2E_CHROMIUM_PATH があればそのブラウザーを使う（playwright install をせずに動かす場合）。</summary>
    public static Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { ExecutablePath = E2ESettings.ChromiumPath });

    /// <summary>
    /// ウィザードを最後まで進め、データベースを作る。ライセンスの確認画面の内容（組織情報など）はテストの出力に出さない。
    /// </summary>
    /// <param name="sampleDatabase">true でサンプルデータベース、false で空のデータベースを作る</param>
    /// <param name="setupTimeout">「実行」から完了までの待ち時間の上限（サンプル DB の作成は数分かかる）</param>
    public async Task RunAsync(string baseUrl, string licenseKey, AdminUser admin, bool sampleDatabase, TimeSpan setupTimeout)
    {
        await _page.GotoAsync($"{baseUrl.TrimEnd('/')}/setup");
        await ExpectStepAsync("セットアップの開始");
        await ClickButtonAsync("次へ");

        await ExpectStepAsync("ライセンスキーの入力");
        await _page.Locator("textarea:visible").First.FillAsync(licenseKey);
        await ClickButtonAsync("次へ");

        await ExpectStepAsync("ライセンス情報の確認", "ライセンスキーが受け付けられませんでした");
        await ClickButtonAsync("次へ");

        await ExpectStepAsync("データベースの作成方法");
        await _page.GetByText(sampleDatabase ? "サンプルデータベースを作成する" : "空のデータベースを作成する", new() { Exact = true })
            .Filter(new() { Visible = true }).ClickAsync();
        await ClickButtonAsync("次へ");

        await ExpectStepAsync("管理ユーザーの登録");
        await _page.GetByLabel("ログイン名:").FillAsync(admin.LoginName);
        await _page.GetByLabel("名前:").FillAsync(admin.DisplayName);
        await _page.GetByLabel("パスワード:", new() { Exact = true }).FillAsync(admin.Password);
        await _page.GetByLabel("パスワードの確認:").FillAsync(admin.Password);
        await ClickButtonAsync("次へ");

        await ExpectStepAsync("実行の確認");
        await ClickButtonAsync("実行");

        // 実行中の画面の後、完了かエラーのどちらかが表示されるまで待つ
        var done = Heading("セットアップの完了");
        var failed = Heading("セットアップエラー");
        await done.Or(failed).WaitForAsync(new() { Timeout = (float)setupTimeout.TotalMilliseconds });
        if (await failed.IsVisibleAsync())
            Assert.Fail($"セットアップエラーになりました: {await VisibleTextAsync()}");
        Log($"セットアップが完了しました（{(sampleDatabase ? "サンプルデータベース" : "空のデータベース")}）");
    }

    /// <summary>完了画面の「管理画面へ移動」から、管理ユーザーでログインする。ログインできたら、ログイン後の画面の文言を返す。</summary>
    public async Task<string> LoginFromCompletionAsync(AdminUser admin)
    {
        await ClickButtonAsync("管理画面へ移動");
        var password = _page.Locator("input[type=password]:visible");
        await password.First.WaitForAsync(new() { Timeout = 120_000 });
        Log($"ログイン画面: {_page.Url}");

        await _page.Locator("input[type=text]:visible, input:not([type]):visible").First.FillAsync(admin.LoginName);
        await password.First.FillAsync(admin.Password);
        await password.First.PressAsync("Enter");

        // ログインできればパスワードの入力欄が消える（失敗すればログイン画面のまま、エラーが表示される）
        try
        {
            await Assertions.Expect(password).ToHaveCountAsync(0, new() { Timeout = 60_000 });
        }
        catch (Exception ex) when (ex is System.TimeoutException or PlaywrightException)
        {
            Assert.Fail($"ログインできませんでした（{_page.Url}）: {await VisibleTextAsync()}");
        }
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var text = await VisibleTextAsync();
        Log($"ログイン後の画面（{_page.Url}）: {text}");
        return text;
    }

    // ウィザードの各画面の見出し。表示中の画面だけを対象にする（ほかの画面も DOM には残っている）
    private ILocator Heading(string title) => _page.GetByText(title, new() { Exact = true }).Filter(new() { Visible = true }).First;

    // 次の画面が表示されるのを待つ。エラーのダイアログ（無効なライセンスキーでの「API通信ができません」など）が出たら、その文言で失敗にする。
    // ライセンスの確認画面の内容（組織情報など）は出さない
    private async Task ExpectStepAsync(string title, string? failureHint = null)
    {
        var dialog = _page.Locator(".x-message-box:visible").First;
        try
        {
            await Heading(title).Or(dialog).WaitForAsync(new() { Timeout = 60_000 });
        }
        catch (Exception ex) when (ex is System.TimeoutException or PlaywrightException)
        {
            Assert.Fail($"「{title}」の画面になりませんでした（{_page.Url}）。");
        }
        if (await dialog.IsVisibleAsync())
            Assert.Fail($"「{title}」の画面の前にエラーが表示されました。{failureHint}: {Regex.Replace(await dialog.InnerTextAsync(), @"\s+", " ").Trim()}");
    }

    private Task ClickButtonAsync(string text) =>
        _page.Locator("a.x-btn:visible").Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(text)}\\s*$") }).First.ClickAsync();

    private async Task<string> VisibleTextAsync() =>
        Regex.Replace(await _page.Locator("body").InnerTextAsync(), @"\s+", " ").Trim() is var t && t.Length > 1500 ? t[..1500] : t;

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
