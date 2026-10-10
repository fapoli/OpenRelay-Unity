using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;

namespace OpenRelay {

public class RelayDriverConstructor : INetworkStreamDriverConstructor {
    private readonly RelayNetworkInterface _interface;

    public RelayDriverConstructor(RelayNetworkInterface networkInterface) {
        _interface = networkInterface;
    }

    public void CreateDriver(
        UnityTransport transport,
        out NetworkDriver driver,
        out NetworkPipeline unreliableFragmentedPipeline,
        out NetworkPipeline unreliableSequencedFragmentedPipeline,
        out NetworkPipeline reliableSequencedPipeline)
    {
        var settings = transport.GetDefaultNetworkSettings();
        driver = NetworkDriver.Create(_interface, settings);

        transport.GetDefaultPipelineConfigurations(
            ref driver,
            out var unreliable,
            out var unreliableSequenced,
            out var reliable);

        unreliableFragmentedPipeline = driver.CreatePipeline(unreliable);
        unreliableSequencedFragmentedPipeline = driver.CreatePipeline(unreliableSequenced);
        reliableSequencedPipeline = driver.CreatePipeline(reliable);
    }
}

}
