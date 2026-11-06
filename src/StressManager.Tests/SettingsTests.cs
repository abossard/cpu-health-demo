using StressManager.Calculations;

namespace StressManager.Tests;

public class SettingsTests
{
    private static Dictionary<string, string?> HealthModelEnv() => new()
    {
        [EnvNames.SubscriptionId] = "s1",
        [EnvNames.ResourceGroup] = "rg-demo",
        [EnvNames.ManagedIdentityId] = "/subscriptions/s1/resourceGroups/rg-demo/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-demo",
        [EnvNames.HealthModelName] = "hm-demo",
        [EnvNames.HealthModelLocation] = "swedencentral",
    };

    [Theory]
    [InlineData("workload=stress", "workload", "stress")]
    [InlineData(" pool=gpu ", "pool", "gpu")]
    [InlineData(null, "workload", "stress")]
    public void JobTemplate_SplitsTheNodeLabel(string? label, string key, string value)
    {
        var env = HealthModelEnv();
        env[Settings.ManagerImageVariable] = "cr.example.io/stress-manager:1";
        env[Settings.NodeLabelVariable] = label;

        var template = Settings.From(k => env.GetValueOrDefault(k)).JobTemplate;

        Assert.Equal((key, value), (template.NodeLabelKey, template.NodeLabelValue));
    }

    [Theory]
    [InlineData("workload")]
    [InlineData("workload=")]
    [InlineData("=stress")]
    [InlineData("a=b=c")]
    [InlineData("a=b,c=d")]
    [InlineData("a=b c")]
    [InlineData("a!=b")]
    [InlineData("a in (b)")]
    public void From_RejectsLabelsThatAreNotOneKeyValue(string label)
    {
        var env = new Dictionary<string, string?> { [Settings.NodeLabelVariable] = label };

        Assert.Throws<FormatException>(() => Settings.From(k => env.GetValueOrDefault(k)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void JobTemplate_NeedsTheManagerImage(string? image)
    {
        var env = HealthModelEnv();
        env[Settings.ManagerImageVariable] = image;

        Assert.Throws<InvalidOperationException>(() => Settings.From(k => env.GetValueOrDefault(k)).JobTemplate);
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { EnvNames.SubscriptionId }, false)]
    [InlineData(new[] { EnvNames.HealthModelName, EnvNames.HealthModelLocation }, false)]
    public void JobTemplate_HealthModelEnvEnablesTheAnnotator(string[] missing, bool enabled)
    {
        var env = HealthModelEnv();
        env[Settings.ManagerImageVariable] = "cr.example.io/stress-manager:1";
        foreach (var name in missing)
            env.Remove(name);
        var manager = Settings.From(k => env.GetValueOrDefault(k));

        // What the pod template hands the annotator container is what Settings reads in annotate mode.
        var annotatorEnv = manager.JobTemplate.HealthModelEnv;
        var annotator = Settings.From(k => annotatorEnv.GetValueOrDefault(k));

        Assert.Equal(enabled, manager.HealthModelEnabled);
        Assert.Equal(enabled, annotator.HealthModelEnabled);
        Assert.Equal(manager.MissingHealthModelVariables, annotator.MissingHealthModelVariables);
    }

    [Fact]
    public void JobTemplate_PassesEveryHealthModelVariable()
    {
        var env = HealthModelEnv();
        env[Settings.ManagerImageVariable] = "cr.example.io/stress-manager:1";

        var passed = Settings.From(k => env.GetValueOrDefault(k)).JobTemplate.HealthModelEnv.Keys;

        Assert.Equal(EnvNames.HealthModel.Order(), passed.Order());
    }
}
