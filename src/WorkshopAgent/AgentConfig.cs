using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WorkshopLink.Protocol;

namespace WorkshopAgent;

internal static class AgentConfig
{
    public static int Apply(string[] args)
    {
        var place = "lan";
        var password = "";
        var account = "";
        var values = args.SkipWhile(arg => !string.Equals(arg, "--configure", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
        if (values.Length > 0)
            place = values[0];
        var parts = new List<string>();
        for (var i = 1; i < values.Length; i++)
        {
            if (string.Equals(values[i], "--account", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < values.Length)
                    account = values[++i];
                continue;
            }

            parts.Add(values[i]);
        }

        password = string.Join(' ', parts);
        if (PasswordPolicy.Problem(password) is not null)
            return 1;
        if (!string.Equals(place, "offsite", StringComparison.OrdinalIgnoreCase))
            place = "lan";
        if (place != "offsite" && MachineFacts.WorkshopIpv4() is null)
            place = "offsite";

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkshopLink");
        Directory.CreateDirectory(folder);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100000, HashAlgorithmName.SHA256, 32);
        var line = "100000:" + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
        var hashPath = Path.Combine(folder, "link.hash");
        File.WriteAllText(hashPath, line, Encoding.ASCII);
        var keyPath = Path.Combine(folder, "link.key");
        File.WriteAllBytes(keyPath, LinkSecret.KeyFromPassword(password));
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.Read | FileSystemRights.Write, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.Read | FileSystemRights.Write, AccessControlType.Allow));
            new FileInfo(hashPath).SetAccessControl(security);
            new FileInfo(keyPath).SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (string.IsNullOrWhiteSpace(account))
            account = WindowsIdentity.GetCurrent().Name;
        var name = account.Split('\\').LastOrDefault() ?? account;
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "SYSTEM", StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(Path.Combine(folder, "account.txt"), name, Encoding.ASCII);
        File.WriteAllText(Path.Combine(folder, "placement.txt"), place, Encoding.ASCII);
        return 0;
    }
}
