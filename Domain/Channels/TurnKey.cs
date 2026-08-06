namespace Domain.Channels;

// Everyone who holds a turn key compares it as an opaque string, but it is minted in two
// processes: voice mints before dispatch so it can recognise its own answer coming back, and the
// conversation group mints for any turn that arrives without one. One mint pins the spelling both
// sides must agree on.
public static class TurnKey
{
    public static string Mint() => Guid.NewGuid().ToString("n");
}