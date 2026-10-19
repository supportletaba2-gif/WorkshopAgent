namespace WorkshopAgent;

internal static class BeaconSender
{
    public static void SendOnce()
    {
        using var link = new LanAnnouncer();
        link.Announce();
    }
}
