namespace Heathen.SteamworksIntegration {
public sealed class LobbyData {
    public static LobbyData Current = new LobbyData();
    public ulong _id = 1;
    public int MyHat { get; set; }
    public int MySkin { get; set; }
    public bool IsValid { get { return true; } }
}
}
