using Avalonia;
using Avalonia.Controls;

namespace BACnetExplorer.CrossPlatform;

public sealed partial class WorkbenchWindow
{
    private readonly ComboBox transport = new() { ItemsSource = new[] { "BACnet / IPv4", "BACnet / MS/TP", "BACnet / IPv6", "BACnet / SC" }, SelectedIndex = 0, Width = 165 };
    private readonly TextBox bind = Input("127.0.0.1:0", "Local IPv4 address:port"), broadcast = Input("255.255.255.255:47808", "Directed broadcast");
    private readonly TextBox network = Input("0", "Link network"), timeout = Input("3000", "Timeout milliseconds"), retries = Input("1", "Retries");
    private readonly TextBox serial = Input("/dev/cu.usbserial", "Serial port"), baud = Input("38400", "Baud"), mac = Input("18", "MAC"), maxMaster = Input("127", "Maximum manager");
    private readonly TextBox bbmd = Input("", "Optional BBMD endpoint"), ttl = Input("120", "Foreign device lifetime");
    private readonly TextBox ipv6Bind = Input("[::1]:47808", "IPv6 bind"), interfaceIndex = Input("1", "Interface index"), vmac = Input("020000000001", "Unused VMAC (hex)");
    private readonly TextBox hub = Input("", "Hub wss:// URL"), failover = Input("", "Failover hub"), certificate = Input("", "Client certificate file"), privateKey = Input("", "Private key file"), siteCa = Input("", "Site CA file"), uuid = Input(Guid.NewGuid().ToString("N"), "UUID");
    private readonly CheckBox verifyName = new() { Content = "Verify hub server name", IsChecked = true };

    private Control ConnectionFields()
    {
        bind.Width = broadcast.Width = 210; network.Width = 75; timeout.Width = 90; retries.Width = 65;
        var ipv4 = Row(Label("IPv4 bind", bind), Label("Directed broadcast", broadcast));
        var mstp = Row(Label("Serial port", serial), Label("Baud", baud), Label("MAC", mac), Label("Maximum manager", maxMaster));
        var ipv6 = Row(Label("IPv6 bind", ipv6Bind), Label("Interface index", interfaceIndex), Label("VMAC", vmac));
        var secure = new StackPanel { Spacing = 6, Children = {
            Row(Label("Hub", hub), Label("Failover", failover), Label("VMAC", scVmac)),
            Row(Label("Certificate", certificate), Label("Private key", privateKey), Label("Site CA", siteCa)),
            Row(Label("UUID", uuid), verifyName)
        } };
        var remote = new Expander { Header = "Foreign device registration", Content = Row(bbmd, ttl) };
        void Update() {
            ipv4.IsVisible = remote.IsVisible = transport.SelectedIndex == 0;
            mstp.IsVisible = transport.SelectedIndex == 1;
            ipv6.IsVisible = transport.SelectedIndex == 2;
            secure.IsVisible = transport.SelectedIndex == 3;
        }
        transport.SelectionChanged += (_, _) => Update(); Update();
        return new StackPanel { Spacing = 5, Children = {
            Row(Label("Network", network), Label("Timeout ms", timeout), Label("Retries", retries)),
            ipv4, mstp, ipv6, secure, remote
        } };
    }
    private readonly TextBox scVmac = Input("020000000002", "Unused VMAC (hex)");
    private ConnectionProfile SelectedProfile() => new() {
        Transport = new[] { "ip", "mstp", "ipv6", "sc" }[transport.SelectedIndex],
        Bind = bind.Text?.Trim() ?? "", Broadcast = broadcast.Text?.Trim() ?? "", Network = (ushort)Number(network,0,65534),
        TimeoutMs = Number(timeout,100,30000), Retries = Number(retries,0,5), Serial = serial.Text?.Trim() ?? "",
        Baud = Number(baud,1200,1000000), Mac = Number(mac,0,127), MaxMaster = Number(maxMaster,0,127),
        Bbmd = bbmd.Text?.Trim() ?? "", Ttl = (ushort)Number(ttl,30,3600),
        Ipv6Bind = ipv6Bind.Text?.Trim() ?? "", InterfaceIndex = Number(interfaceIndex,1,int.MaxValue),
        Vmac = transport.SelectedIndex == 3 ? scVmac.Text?.Trim() ?? "" : vmac.Text?.Trim() ?? "",
        Hub = hub.Text?.Trim() ?? "", FailoverHub = failover.Text?.Trim() ?? "", Certificate = certificate.Text?.Trim() ?? "",
        PrivateKey = privateKey.Text?.Trim() ?? "", SiteCa = siteCa.Text?.Trim() ?? "", Uuid = uuid.Text?.Trim() ?? "",
        VerifyServerName = verifyName.IsChecked == true
    };
    private void ApplyProfile(ConnectionProfile profile)
    {
        transport.SelectedIndex = Array.IndexOf(new[] { "ip", "mstp", "ipv6", "sc" }, profile.Transport);
        if (transport.SelectedIndex < 0) throw new InvalidDataException("Unknown workspace transport.");
        bind.Text = profile.Bind; broadcast.Text = profile.Broadcast; network.Text = profile.Network.ToString();
        timeout.Text = profile.TimeoutMs.ToString(); retries.Text = profile.Retries.ToString();
        serial.Text = profile.Serial; baud.Text = profile.Baud.ToString(); mac.Text = profile.Mac.ToString(); maxMaster.Text = profile.MaxMaster.ToString();
        bbmd.Text = profile.Bbmd; ttl.Text = profile.Ttl.ToString(); ipv6Bind.Text = profile.Ipv6Bind; interfaceIndex.Text = profile.InterfaceIndex.ToString();
        vmac.Text = scVmac.Text = profile.Vmac; hub.Text = profile.Hub; failover.Text = profile.FailoverHub;
        certificate.Text = profile.Certificate; privateKey.Text = profile.PrivateKey; siteCa.Text = profile.SiteCa;
        uuid.Text = profile.Uuid; verifyName.IsChecked = profile.VerifyServerName;
    }
}
