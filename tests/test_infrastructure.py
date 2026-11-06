import json
import os
import shlex
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class InfrastructureTests(unittest.TestCase):
    # C2, C4, C5
    def test_C2_C4_C5_bicep_builds_three_vms_without_public_inbound(self):
        root_result = subprocess.run(
            ["bicep", "build", "infra/main.bicep", "--stdout"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
        )
        compute_result = subprocess.run(
            ["bicep", "build", "infra/compute.bicep", "--stdout"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
        )

        self.assertEqual(root_result.returncode, 0, root_result.stderr)
        self.assertEqual(compute_result.returncode, 0, compute_result.stderr)
        root_template = json.loads(root_result.stdout)
        template = json.loads(compute_result.stdout)
        resources = template["resources"]
        root_resources = root_template["resources"]
        self.assertTrue(
            any(resource["type"].lower() == "microsoft.resources/resourcegroups"
                for resource in root_resources)
        )
        self.assertTrue(
            any(resource["type"].lower() == "microsoft.resources/deployments"
                for resource in root_resources)
        )
        virtual_machines = [
            resource for resource in resources
            if resource["type"].lower() == "microsoft.compute/virtualmachines"
        ]
        public_ips = [
            resource for resource in resources
            if resource["type"].lower() == "microsoft.network/publicipaddresses"
        ]
        self.assertEqual(len(virtual_machines), 1)
        self.assertEqual(len(public_ips), 1)
        self.assertEqual(
            root_template["parameters"]["vmNames"]["defaultValue"],
            ["cpu-health-demo-1", "cpu-health-demo-2", "cpu-health-demo-3"],
        )
        self.assertEqual(
            virtual_machines[0]["copy"]["count"],
            "[length(parameters('vmNames'))]",
        )
        self.assertEqual(
            public_ips[0]["copy"]["count"],
            "[length(parameters('vmNames'))]",
        )
        self.assertEqual(
            virtual_machines[0]["properties"]["hardwareProfile"]["vmSize"],
            "Standard_D2s_v5",
        )

        nsg = next(
            resource for resource in resources
            if resource["type"].lower() == "microsoft.network/networksecuritygroups"
        )
        self.assertEqual(nsg["properties"]["securityRules"], [])
        cloud_init = (ROOT / "infra/cloud-init.yaml").read_text()
        self.assertNotIn("nginx", cloud_init.lower())
        self.assertNotIn("--publish", cloud_init)

    @unittest.skipUnless(shutil.which("dash"), "dash is required to exercise cloud-init's shell")
    def test_C6_C7_cloud_init_starts_the_stress_schedule(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            bin_path = temp_path / "bin"
            bin_path.mkdir()
            commands = temp_path / "commands"
            stress_dir = temp_path / "stress"
            for name in ("docker", "systemctl"):
                command = bin_path / name
                command.write_text(
                    f"#!/bin/sh\nprintf '%s\\n' '{name} '\"$*\" >> \"$COMMAND_LOG\"\n"
                )
                command.chmod(0o755)
            env = os.environ.copy()
            env.update({"PATH": f"{bin_path}:{os.environ['PATH']}", "COMMAND_LOG": str(commands)})
            cloud_init = (ROOT / "infra/cloud-init.yaml").read_text()
            bootstrap = textwrap.dedent(cloud_init.split("runcmd:\n  - |\n", 1)[1])
            bootstrap = bootstrap.replace("/opt/cpu-health-demo/stress", shlex.quote(str(stress_dir)))

            result = subprocess.run(
                [shutil.which("dash"), "-c", bootstrap],
                cwd=ROOT,
                env=env,
                capture_output=True,
                text=True,
                timeout=10,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            calls = commands.read_text().splitlines()
            self.assertEqual(calls[0], "systemctl enable --now docker.service")
            self.assertTrue(calls[1].startswith("docker build --pull -t cpu-health-stress:local "))
            self.assertEqual(calls[-1], "systemctl enable --now cpu-health-stress.service")
            dockerfile = (stress_dir / "Dockerfile").read_text()
            self.assertIn("stress-ng", dockerfile)
            self.assertNotIn("ENTRYPOINT", dockerfile)

    # C3, C11
    def test_C3_C11_deployment_script_defaults_to_preflight_only(self):
        source = (ROOT / "scripts/deploy.sh").read_text()

        self.assertIn("az vm list-skus", source)
        self.assertIn("az vm list-usage", source)
        self.assertIn("az policy assignment list", source)
        self.assertIn("what-if", source)
        self.assertIn("CPU_HEALTH_DEPLOY_CONFIRMATION", source)
        self.assertIn("fixed three-node pool", source)
        self.assertIn("recheck candidate node SKU restrictions and quota", source)
        self.assertIn("separate approval", source)
        self.assertIn("az deployment", source)


if __name__ == "__main__":
    unittest.main()
