import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class HealthModelTests(unittest.TestCase):
    # C9, C12
    def test_C9_C12_cpu_model_compiles_with_reviewed_thresholds_and_cadence(self):
        result = subprocess.run(
            ["bicep", "build", "infra/healthmodel.bicep", "--stdout"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        template = json.loads(result.stdout)
        definitions = [
            resource for resource in template["resources"]
            if resource["type"].lower()
            == "microsoft.cloudhealth/healthmodels/signaldefinitions"
        ]
        self.assertEqual(len(definitions), 1)
        signal = definitions[0]["properties"]
        self.assertEqual(signal["metricName"], "Percentage CPU")
        self.assertEqual(signal["aggregationType"], "Average")
        self.assertEqual(signal["timeGrain"], "PT5M")
        self.assertEqual(signal["refreshInterval"], "PT1M")
        self.assertEqual(signal["dataUnit"], "Percent")
        self.assertEqual(
            signal["evaluationRules"]["degradedRule"]["operator"],
            "GreaterThan",
        )
        self.assertEqual(
            signal["evaluationRules"]["unhealthyRule"]["operator"],
            "GreaterThan",
        )
        self.assertEqual(signal["evaluationRules"]["degradedRule"]["threshold"], 90)
        self.assertEqual(signal["evaluationRules"]["unhealthyRule"]["threshold"], 98)
        assignments = [
            resource for resource in template["resources"]
            if resource["type"].lower() == "microsoft.authorization/roleassignments"
        ]
        self.assertEqual(len(assignments), 1)
        self.assertEqual(
            assignments[0]["copy"]["count"],
            "[length(parameters('vmNames'))]",
        )
        self.assertIn(
            "Microsoft.Compute/virtualMachines",
            assignments[0]["scope"],
        )


if __name__ == "__main__":
    unittest.main()
