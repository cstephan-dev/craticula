using Craticula.Integration.Tests.Docker;

namespace Craticula.Integration.Tests.Containers;

/// <summary>
/// The Docker gate (#2205). Lives in the <c>Containers</c> namespace with every test that starts a container, so
/// the offline filter in <c>CLAUDE.md</c> (<c>FullyQualifiedName!~Integration.Tests.Containers</c>) leaves them
/// out while the loader and comparer tests above stay in it.
/// </summary>
public class DockerGateTests
{
    [DockerFact]
    public void The_Docker_Daemon_Answers_When_This_Test_Runs()
    {
        // Reaching this body means the gate saw a daemon; without one xUnit reports the test skipped, with the reason.
        Assert.True(DockerAvailability.IsAvailable);
        Assert.Null(DockerAvailability.UnavailableReason);
    }

    [Fact]
    public void The_Reason_Names_The_Cause_When_Docker_Is_Unavailable()
    {
        // Not Docker-gated on purpose: it holds on every machine.
        Assert.Equal(DockerAvailability.UnavailableReason is null, DockerAvailability.IsAvailable);
        var attribute = new DockerFactAttribute();
        Assert.Equal(DockerAvailability.IsAvailable, attribute.Skip is null);
        if (!DockerAvailability.IsAvailable)
        {
            Assert.Contains(DockerAvailability.UnavailableReason!, attribute.Skip);
        }
    }
}
