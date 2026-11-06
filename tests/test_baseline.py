import subprocess
import unittest


class ProjectBootstrapTests(unittest.TestCase):
    def test_C1_approved_branch_is_checked_out(self):
        result = subprocess.run(
            ["git", "symbolic-ref", "--short", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.stdout.strip(), "demo/cpu-health-demo")

    def test_C1_repository_has_no_commit_at_baseline(self):
        result = subprocess.run(
            ["git", "rev-parse", "--verify", "HEAD"],
            capture_output=True,
            text=True,
        )
        self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
