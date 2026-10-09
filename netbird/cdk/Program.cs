using Amazon.CDK;

namespace Netbird.Cdk;

public static class Program
{
    public static void Main()
    {
        var app = new App();

        // Netbird lives in the autoguru-shared account (791686214595), ap-southeast-2.
        // A concrete account + region is required because the EC2 instances and Elastic IPs are
        // account/region bound. The account is pinned (rather than read from ambient credentials)
        // so a stray set of credentials cannot deploy this internet-facing VPN edge elsewhere.
        var env = new Amazon.CDK.Environment
        {
            Account = "791686214595",
            Region = "ap-southeast-2",
        };

        // Termination protection (COM-219 deploy guard): deleting either stack destroys a stateful
        // instance (the control-plane datastore, or the routing-peer identity every network router
        // is bound to). With protection on, `cdk destroy` and DeleteStack fail until someone turns
        // it off on purpose. CDK sets it on the stack when it prepares the change set; it changes
        // no resource.
        _ = new NetbirdControlPlaneStack(app, "NetbirdControlPlaneStack",
            new StackProps { Env = env, TerminationProtection = true });
        _ = new NetbirdRoutingPeerStack(app, "NetbirdRoutingPeerStack",
            new StackProps { Env = env, TerminationProtection = true });

        app.Synth();
    }
}
