import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class VerificationTests(unittest.TestCase):
    # C8, C10
    def test_C8_C10_help_and_invalid_arguments_do_not_contact_azure(self):
        cases = [
            ("scripts/verify.sh", ["--help"], 0, "PT1M"),
            ("scripts/verify.sh", ["--unknown"], 2, "Unknown argument"),
            ("scripts/deploy.sh", ["--help"], 0, "what-if"),
            ("scripts/deploy.sh", ["--unknown"], 2, "Unknown argument"),
        ]
        for script, arguments, expected_code, expected_text in cases:
            with self.subTest(script=script, arguments=arguments):
                result = subprocess.run(
                    ["bash", script, *arguments],
                    cwd=ROOT,
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
                self.assertEqual(result.returncode, expected_code)
                self.assertIn(expected_text, result.stdout + result.stderr)

    def test_C8_C11_health_model_deployment_requires_exact_confirmation(self):
        source = (ROOT / "scripts/deploy.sh").read_text()

        self.assertIn("/verify.sh", source)
        self.assertIn("az deployment group what-if", source)
        self.assertIn("CPU_HEALTH_MODEL_MONTHLY_COST_ESTIMATE", source)
        self.assertIn("CPU_HEALTH_MODEL_DEPLOY_CONFIRMATION", source)
        self.assertIn("HEALTHMODEL_TEMPLATE", source)

    def test_C11_model_deployment_stops_without_exact_confirmation(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            bin_path = temp_path / "bin"
            bin_path.mkdir()
            create_log = temp_path / "create.log"
            fake_az = bin_path / "az"
            fake_az.write_text(
                "#!/bin/sh\n"
                "case \"$*\" in\n"
                "  'account show'*) printf '%s\\n' "
                "'{\"id\":\"00000000-0000-0000-0000-000000000001\","
                "\"tenantId\":\"00000000-0000-0000-0000-000000000002\"}' ;;\n"
                "  'vm list'*) printf '%s\\n' "
                "'[{\"name\":\"cpu-health-demo-1\",\"id\":\"/vm/1\"},"
                "{\"name\":\"cpu-health-demo-2\",\"id\":\"/vm/2\"},"
                "{\"name\":\"cpu-health-demo-3\",\"id\":\"/vm/3\"}]' ;;\n"
                "  'monitor metrics list-definitions'*) printf '%s\\n' "
                "'[{\"name\":{\"value\":\"Percentage CPU\"},\"unit\":\"Percent\","
                "\"supportedAggregationTypes\":[\"Average\"],"
                "\"metricAvailabilities\":[{\"timeGrain\":\"PT1M\"},{\"timeGrain\":\"PT5M\"}]}]' ;;\n"
                "  'monitor metrics list'*) printf '%s\\n' "
                "'{\"value\":[{\"timeseries\":[{\"data\":[{\"average\":10}]}]}]}' ;;\n"
                "  'group exists'*) printf 'true\\n' ;;\n"
                "  'resource list'*) printf '\\n' ;;\n"
                "  'deployment group what-if'*) printf 'mock what-if\\n' ;;\n"
                "  'deployment group create'*) printf 'create\\n' >> \"$CREATE_LOG\" ;;\n"
                "  *) printf 'unexpected az command: %s\\n' \"$*\" >&2; exit 99 ;;\n"
                "esac\n"
            )
            fake_az.chmod(0o755)
            fake_curl = bin_path / "curl"
            fake_curl.write_text("#!/bin/sh\nexit 1\n")
            fake_curl.chmod(0o755)

            env = os.environ.copy()
            env.update(
                {
                    "PATH": f"{bin_path}:{os.environ['PATH']}",
                    "CREATE_LOG": str(create_log),
                    "CPU_HEALTH_MODEL_MONTHLY_COST_ESTIMATE": "estimate required",
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000001",
                    "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
                }
            )
            result = subprocess.run(
                ["bash", "scripts/deploy.sh", "--health-model", "--deploy"],
                cwd=ROOT,
                env=env,
                capture_output=True,
                text=True,
                timeout=15,
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("No Azure resources were changed", result.stderr)
            self.assertFalse(create_log.exists())

    # C10, C12
    def test_C10_C12_live_verification_requires_degraded_state_and_recovery(self):
        source = (ROOT / "scripts/verify.sh").read_text()

        self.assertIn("REQUIRED_RUNTIME_MINUTES=81", source)
        self.assertIn('health_state" != "Degraded"', source)
        self.assertIn('health_state" == "Healthy"', source)
        self.assertIn("Percentage CPU", source)
        self.assertIn("cpu-health-demo-1", source)
        self.assertIn("list-definitions", source)
        self.assertIn("PT1M", source)
        self.assertIn("PT5M", source)


if __name__ == "__main__":
    unittest.main()
