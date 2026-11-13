using System.Security.AccessControl;
using System.Security.Principal;
using OpenMagicMouse.Backend;
using OpenMagicMouse.UI;

namespace OpenMagicMouse;

internal static class Program
{
    public const string ShowUiEventName = @"Local\OpenMagicMouseShowUI";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
        {
            WindowsServiceHost.Run();
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            bool checkPipe = args.Any(a => string.Equals(a, "--check-pipe", StringComparison.OrdinalIgnoreCase));
            return SelfTestRunner.Run(checkPipe);
        }

        bool startMinimized = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        var evSec = new EventWaitHandleSecurity();
        evSec.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.FullControl,
            AccessControlType.Allow));
        using var showUiEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowUiEventName, out _, evSec);
        using var singleInstance = new Mutex(true, @"Local\OpenMagicMouseTrayUI", out bool isNewInstance);

        if (!isNewInstance)
        {
            if (!startMinimized)
            {
                showUiEvent.Set();
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var form = new ControlCenterForm(startMinimized, showUiEvent);
        Application.Run(form);
        return 0;
    }
}
