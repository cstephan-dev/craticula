namespace Craticula.Integration.Tests.Docker;

/// <summary><c>[Fact]</c> that is reported as skipped, with the reason, when no Docker daemon answers.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = $"Docker is unavailable: {DockerAvailability.UnavailableReason}";
        }
    }
}

/// <summary><c>[Theory]</c> that is reported as skipped, with the reason, when no Docker daemon answers.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = $"Docker is unavailable: {DockerAvailability.UnavailableReason}";
        }
    }
}
