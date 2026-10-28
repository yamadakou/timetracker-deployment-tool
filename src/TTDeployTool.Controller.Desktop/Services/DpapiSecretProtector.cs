using System.Security.Cryptography;
using System.Text;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// 保存するパスワードを、Windows の DPAPI（<see cref="ProtectedData"/>、今の Windows ユーザー）で暗号化する（docs/design/desktop/desktop-jobs.md の 6.7、D25）。
/// 同じ PC の同じ Windows ユーザーだけが復号できる。ほかのアプリの DPAPI のデータと混ざらないよう、このアプリの固定の値（entropy）を添える。
/// </summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TTDeployTool.ScheduledDeployment.v1");

    public string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("保存した値の形式が正しくありません。", ex);
        }
    }
}
