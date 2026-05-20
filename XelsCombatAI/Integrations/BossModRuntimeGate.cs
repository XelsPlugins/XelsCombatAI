namespace XelsCombatAI.Integrations;

internal sealed class BossModRuntimeGate
{
    private volatile bool isOpen;

    public bool IsOpen => this.isOpen;

    public void Open()
    {
        this.isOpen = true;
    }

    public void Close()
    {
        this.isOpen = false;
    }
}
