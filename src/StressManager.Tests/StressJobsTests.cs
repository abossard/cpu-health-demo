using StressManager.Calculations;

namespace StressManager.Tests;

public class StressJobsTests
{
    [Theory]
    [InlineData("rhythm:3", "rhythm-3")]
    [InlineData("rhythm:0", "rhythm-0")]
    public void Slug_IsLabelSafe(string source, string expected) =>
        Assert.Equal(expected, StressJobs.Slug(source));

    [Theory]
    [InlineData(1, 40, 120)]
    [InlineData(2, 100, 180)]
    [InlineData(10, 580, 660)]
    [InlineData(60, 3580, 3660)]
    public void Timing_EndsBeforeTheNextFiring(int minutes, int timeoutSeconds, int deadlineSeconds)
    {
        Assert.Equal(timeoutSeconds, StressJobs.TimeoutSeconds(minutes));
        Assert.Equal(deadlineSeconds, StressJobs.ActiveDeadlineSeconds(minutes));
    }

    [Theory]
    [InlineData(10, 100, "rhythm:0", 3)]
    [InlineData(1, 40, "rhythm:2", 3)]
    [InlineData(60, 75, "rhythm:11", 2)]
    public void BuildTemplate_Shape(int minutes, int load, string source, int nodes)
    {
        var job = StressJobs.BuildTemplate(new StressRequest(minutes, load, source), nodes, TestData.Config);

        Assert.Equal(("stress-run", StressJobs.Slug(source)), (job.Metadata.Labels["app"], job.Metadata.Labels["stress/source"]));
        Assert.Equal((minutes.ToString(), load.ToString(), source),
            (job.Metadata.Annotations["stress/minutes"], job.Metadata.Annotations["stress/load"], job.Metadata.Annotations["stress/source"]));
        var spec = job.Spec;
        Assert.Equal(("Indexed", nodes, nodes), (spec.CompletionMode, spec.Completions, spec.Parallelism));
        Assert.Equal((0, nodes, (int?)null), (spec.BackoffLimitPerIndex!.Value, spec.MaxFailedIndexes!.Value, spec.BackoffLimit));
        Assert.Equal((minutes * 60 + 60L, 3600), (spec.ActiveDeadlineSeconds!.Value, spec.TtlSecondsAfterFinished!.Value));
        var rule = Assert.Single(spec.PodFailurePolicy.Rules);
        Assert.Equal(("Ignore", "DisruptionTarget"), (rule.Action, Assert.Single(rule.OnPodConditions).Type));

        var meta = spec.Template.Metadata;
        Assert.Equal(("true", "stress-run"), (meta.Labels["azure.workload.identity/use"], meta.Labels["app"]));
        Assert.Equal("stress", meta.Annotations["azure.workload.identity/skip-containers"]);
        var pod = spec.Template.Spec;
        Assert.Equal(("stress-runner", false, "Never", 30L),
            (pod.ServiceAccountName, pod.AutomountServiceAccountToken!.Value, pod.RestartPolicy, pod.TerminationGracePeriodSeconds!.Value));
        Assert.Equal((true, "RuntimeDefault"), (pod.SecurityContext.RunAsNonRoot!.Value, pod.SecurityContext.SeccompProfile.Type));
        Assert.Equal("stress", pod.NodeSelector["workload"]);
        var toleration = Assert.Single(pod.Tolerations);
        Assert.Equal(("workload", "Equal", "stress", "NoSchedule"), (toleration.Key, toleration.OperatorProperty, toleration.Value, toleration.Effect));
        var antiAffinity = Assert.Single(pod.Affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution);
        Assert.Equal(("kubernetes.io/hostname", "stress-run"), (antiAffinity.TopologyKey, antiAffinity.LabelSelector.MatchLabels["app"]));
        Assert.Equal(["batch.kubernetes.io/controller-uid"], antiAffinity.MatchLabelKeys);
        Assert.Equal(["run-stress", "tmp"], pod.Volumes.Select(v => v.Name));

        var sidecar = Assert.Single(pod.InitContainers);
        Assert.Equal(("annotator", "cr.example.io/stress-manager:1", "Always"), (sidecar.Name, sidecar.Image, sidecar.RestartPolicy));
        Assert.Equal(["annotate"], sidecar.Args);
        Assert.Equal(["/run/stress", "/tmp"], sidecar.VolumeMounts.Select(m => m.MountPath));
        var env = sidecar.Env.ToDictionary(e => e.Name);
        Assert.Equal("spec.nodeName", env[EnvNames.NodeName].ValueFrom.FieldRef.FieldPath);
        Assert.Equal("metadata.labels['batch.kubernetes.io/job-name']", env[EnvNames.RunId].ValueFrom.FieldRef.FieldPath);
        Assert.Equal((minutes.ToString(), load.ToString(), source, "/run/stress/started"),
            (env[EnvNames.Minutes].Value, env[EnvNames.Load].Value, env[EnvNames.Source].Value, env[EnvNames.StartedFile].Value));
        Assert.Equal(("s1", "rg-demo", "hm-demo"),
            (env[EnvNames.SubscriptionId].Value, env[EnvNames.ResourceGroup].Value, env[EnvNames.HealthModelName].Value));

        var stress = Assert.Single(pod.Containers);
        Assert.Equal(("stress", StressJobs.StressNgImage), (stress.Name, stress.Image));
        Assert.Equal(["/bin/sh", "-c", "touch \"$STARTED_FILE\" && exec /usr/bin/stress-ng \"$@\"", "stress-ng"], stress.Command);
        Assert.Equal(["--cpu", "0", "--cpu-load", load.ToString(), "--timeout", $"{StressJobs.TimeoutSeconds(minutes)}s",
            "--temp-path", "/run/stress", "--metrics-brief"], stress.Args);
        Assert.Equal("/run/stress/started", Assert.Single(stress.Env).Value);
        Assert.Equal(["/run/stress"], stress.VolumeMounts.Select(m => m.MountPath));
        Assert.Equal((65534L, 65534L), (stress.SecurityContext.RunAsUser!.Value, stress.SecurityContext.RunAsGroup!.Value));
        Assert.Null(sidecar.SecurityContext.RunAsUser);
        Assert.All([stress.SecurityContext, sidecar.SecurityContext], c =>
        {
            Assert.Equal((true, false, true), (c.RunAsNonRoot!.Value, c.AllowPrivilegeEscalation!.Value, c.ReadOnlyRootFilesystem!.Value));
            Assert.Equal(["ALL"], c.Capabilities.Drop);
        });
    }

    [Theory]
    [InlineData("node-pool", "cpu")]
    [InlineData("workload", "stress")]
    public void BuildTemplate_NodeLabelDrivesSelectorAndToleration(string key, string value)
    {
        var pod = StressJobs.BuildTemplate(new StressRequest(5, 100, "rhythm:0"), 3,
            TestData.Config with { NodeLabelKey = key, NodeLabelValue = value }).Spec.Template.Spec;

        Assert.Equal(value, pod.NodeSelector[key]);
        Assert.Equal((key, value), (pod.Tolerations[0].Key, pod.Tolerations[0].Value));
    }

    [Fact]
    public void StressImage_IsPinnedByDigest()
    {
        Assert.StartsWith("ghcr.io/colinianking/stress-ng:", StressJobs.StressNgImage);
        Assert.Matches("@sha256:[0-9a-f]{64}$", StressJobs.StressNgImage);
    }
}
