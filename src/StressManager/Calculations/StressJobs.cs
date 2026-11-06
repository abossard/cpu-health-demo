using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Models;

namespace StressManager.Calculations;

/// <summary>Source is e.g. "rhythm:3".</summary>
public sealed record StressRequest(int Minutes, int Load, string Source);

/// <summary>HealthModelEnv is passed to the annotator sidecar as is.</summary>
public sealed record JobTemplateConfig(
    string ManagerImage,
    string StressImage,
    string NodeLabelKey,
    string NodeLabelValue,
    IReadOnlyDictionary<string, string> HealthModelEnv);

/// <summary>The stress Job template every rhythm CronJob embeds.</summary>
public static class StressJobs
{
    /// <summary>github.com/ColinIanKing/stress-ng release V0.22.02.</summary>
    public const string StressNgImage =
        "ghcr.io/colinianking/stress-ng:e1a93515da37db1e1b0d07f6ed932c83b7cefd3d@sha256:60f60a04a44b16b5325d53a12ddfc5de7ad16f24418f3de4b852277a780b5ced";

    public const string AppLabelValue = "stress-run";
    public const string Selector = "app=" + AppLabelValue;
    public const string SourceLabel = "stress/source";
    public const string MinutesAnnotation = "stress/minutes";
    public const string LoadAnnotation = "stress/load";
    public const string SourceAnnotation = "stress/source";
    public const string JobNameLabel = "batch.kubernetes.io/job-name";
    public const string ControllerUidLabel = "batch.kubernetes.io/controller-uid";
    public const string SkipContainersAnnotation = "azure.workload.identity/skip-containers";
    public const string ServiceAccount = "stress-runner";
    public const string RunDirectory = "/run/stress";
    public const string StartedFile = RunDirectory + "/started";
    /// <summary>nobody in the Debian-based stress-ng image, which sets no USER.</summary>
    public const long StressUid = 65534;
    public const int TtlSecondsAfterFinished = 3600;

    /// <summary>Writes the start signal for the annotator, then execs stress-ng with the container args.</summary>
    public const string StressScript = $"touch \"${EnvNames.StartedFile}\" && exec /usr/bin/stress-ng \"$@\"";

    /// <summary>Label-safe form of a source: "rhythm:3" -> "rhythm-3".</summary>
    public static string Slug(string source) => source.Replace(':', '-');

    /// <summary>Ends 20 s before the next firing of a step that fills its whole cycle, so Forbid does not skip it.</summary>
    public static int TimeoutSeconds(int minutes) => Math.Max(30, minutes * 60 - 20);

    public static int ActiveDeadlineSeconds(int minutes) => minutes * 60 + 60;

    public static IList<string> StressArgs(int minutes, int load) =>
    [
        "--cpu", "0", "--cpu-load", load.ToString(CultureInfo.InvariantCulture),
        "--timeout", $"{TimeoutSeconds(minutes)}s", "--temp-path", RunDirectory, "--metrics-brief",
    ];

    /// <summary>
    /// Changes when anything outside the rhythm changes the Job: images, health model env, node count, node label,
    /// or this template's code. Hashes the template built for a fixed probe request.
    /// </summary>
    public static string TemplateHash(int nodeCount, JobTemplateConfig config) =>
        Hash(KubernetesJson.Serialize(BuildTemplate(new StressRequest(1, 1, "rhythm:0"), nodeCount, config)));

    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>completions = parallelism = nodeCount; one pod per node.</summary>
    public static V1JobTemplateSpec BuildTemplate(StressRequest request, int nodeCount, JobTemplateConfig config)
    {
        var minutes = request.Minutes.ToString(CultureInfo.InvariantCulture);
        var load = request.Load.ToString(CultureInfo.InvariantCulture);
        var slug = Slug(request.Source);
        var runVolume = new V1VolumeMount { Name = "run-stress", MountPath = RunDirectory };
        return new V1JobTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { ["app"] = AppLabelValue, [SourceLabel] = slug },
                Annotations = new Dictionary<string, string>
                {
                    [MinutesAnnotation] = minutes,
                    [LoadAnnotation] = load,
                    [SourceAnnotation] = request.Source,
                },
            },
            Spec = new V1JobSpec
            {
                CompletionMode = "Indexed",
                Completions = nodeCount,
                Parallelism = nodeCount,
                // One failed pod fails only its own index; the other nodes keep stressing.
                BackoffLimitPerIndex = 0,
                MaxFailedIndexes = nodeCount,
                ActiveDeadlineSeconds = ActiveDeadlineSeconds(request.Minutes),
                TtlSecondsAfterFinished = TtlSecondsAfterFinished,
                PodFailurePolicy = new V1PodFailurePolicy
                {
                    Rules =
                    [
                        new V1PodFailurePolicyRule
                        {
                            Action = "Ignore",
                            OnPodConditions = [new V1PodFailurePolicyOnPodConditionsPattern { Type = "DisruptionTarget", Status = "True" }],
                        },
                    ],
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>
                        {
                            ["app"] = AppLabelValue,
                            [SourceLabel] = slug,
                            ["azure.workload.identity/use"] = "true",
                        },
                        Annotations = new Dictionary<string, string> { [SkipContainersAnnotation] = "stress" },
                    },
                    Spec = new V1PodSpec
                    {
                        ServiceAccountName = ServiceAccount,
                        AutomountServiceAccountToken = false,
                        RestartPolicy = "Never",
                        TerminationGracePeriodSeconds = 30,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
                        },
                        NodeSelector = new Dictionary<string, string> { [config.NodeLabelKey] = config.NodeLabelValue },
                        Tolerations =
                        [
                            new V1Toleration
                            {
                                Key = config.NodeLabelKey, OperatorProperty = "Equal", Value = config.NodeLabelValue, Effect = "NoSchedule",
                            },
                        ],
                        Affinity = new V1Affinity
                        {
                            PodAntiAffinity = new V1PodAntiAffinity
                            {
                                RequiredDuringSchedulingIgnoredDuringExecution =
                                [
                                    new V1PodAffinityTerm
                                    {
                                        TopologyKey = "kubernetes.io/hostname",
                                        LabelSelector = new V1LabelSelector
                                        {
                                            MatchLabels = new Dictionary<string, string> { ["app"] = AppLabelValue },
                                        },
                                        // Only pods of the same Job repel each other.
                                        MatchLabelKeys = [ControllerUidLabel],
                                    },
                                ],
                            },
                        },
                        Volumes =
                        [
                            new V1Volume { Name = "run-stress", EmptyDir = new V1EmptyDirVolumeSource() },
                            new V1Volume { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() },
                        ],
                        InitContainers =
                        [
                            new V1Container
                            {
                                Name = "annotator",
                                Image = config.ManagerImage,
                                Args = ["annotate"],
                                RestartPolicy = "Always",
                                Env = AnnotatorEnv(minutes, load, request.Source, config),
                                SecurityContext = Restricted(null),
                                VolumeMounts = [runVolume, new V1VolumeMount { Name = "tmp", MountPath = "/tmp" }],
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("64Mi") },
                                },
                            },
                        ],
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "stress",
                                Image = config.StressImage,
                                Command = ["/bin/sh", "-c", StressScript, "stress-ng"],
                                Args = StressArgs(request.Minutes, request.Load),
                                Env = [new V1EnvVar { Name = EnvNames.StartedFile, Value = StartedFile }],
                                SecurityContext = Restricted(StressUid),
                                VolumeMounts = [runVolume],
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m") },
                                },
                            },
                        ],
                    },
                },
            },
        };
    }

    private static V1SecurityContext Restricted(long? uid) => new()
    {
        RunAsNonRoot = true,
        RunAsUser = uid,
        RunAsGroup = uid,
        AllowPrivilegeEscalation = false,
        ReadOnlyRootFilesystem = true,
        Capabilities = new V1Capabilities { Drop = ["ALL"] },
    };

    private static IList<V1EnvVar> AnnotatorEnv(string minutes, string load, string source, JobTemplateConfig config) =>
        new List<V1EnvVar>
        {
            new() { Name = EnvNames.NodeName, ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = "spec.nodeName" } } },
            new() { Name = EnvNames.RunId, ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = $"metadata.labels['{JobNameLabel}']" } } },
            new() { Name = EnvNames.Minutes, Value = minutes },
            new() { Name = EnvNames.Load, Value = load },
            new() { Name = EnvNames.Source, Value = source },
            new() { Name = EnvNames.StartedFile, Value = StartedFile },
        }
        .Concat(config.HealthModelEnv.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new V1EnvVar { Name = e.Key, Value = e.Value }))
        .ToList();
}
