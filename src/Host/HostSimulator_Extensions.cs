// Expose AuthoritativeGameState to LateJoinHandler
namespace CairnCoop.Host
{
    public sealed partial class HostSimulator
    {
        public AuthoritativeGameState State => _state;
    }
}
