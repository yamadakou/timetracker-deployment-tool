using Xunit;
using static TTDeployTool.Controller.Desktop.UITests.MockUiTests;

namespace TTDeployTool.Controller.Desktop.UITests;

/// <summary>
/// 「新しくデプロイ」の手順の帯（No.91。Blazor 版の desktop-blazor-hybrid.md の 16.20 と同じ仕様）のモックの UI テスト。
/// 移る先の判断（前へはいつでも、先へは誤りのある手順で止める、完了の表示では押せない）は Core の WizardTests で確かめている。
/// ここでは、帯のボタンが Core の GoToStepAsync につながることを見る。
/// </summary>
public class MockWizardStepTests
{
    [Fact]
    public void The_Step_Band_Should_Stop_At_The_First_Step_With_An_Error_And_Go_Back_Freely() => WithFixture(app =>
    {
        SignIn(app);
        app.Show("Wizard");
        app.Find("Panel.Wizard");
        AppSession.WaitUntil(() => app.ItemsOf("Target.Subscription").Count > 0, "サブスクリプションの一覧が読まれる");
        app.Select("Target.Subscription", app.ItemsOf("Target.Subscription")[0]);
        app.SetText("Target.ResourceGroup", "rg-mock-steps");
        app.SetText("Target.AppName", "steps");
        app.SetText("Target.Location", "japaneast");

        // パスワードが未入力のため、「確認と実行」を押しても手順「パスワード」で止まる
        app.Invoke("Wizard.GoTo.Confirm");
        app.WaitForText("Wizard.Step", t => t.StartsWith("パスワード（3", StringComparison.Ordinal), "誤りのある手順で止まる");

        // 前の手順へは、いつでも移る
        app.Invoke("Wizard.GoTo.Target");
        app.WaitForText("Wizard.Step", t => t.StartsWith("対象（1", StringComparison.Ordinal), "前の手順に戻る");
        app.Invoke("Wizard.GoTo.Db");
        app.WaitForText("Wizard.Step", t => t.StartsWith("DB（2", StringComparison.Ordinal), "誤りの無い先の手順へは移る");
    });
}
