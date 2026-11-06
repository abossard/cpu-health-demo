import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class StressRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.bin_dir = Path(self.temp_dir.name) / "bin"
        self.bin_dir.mkdir()
        self.docker_log = Path(self.temp_dir.name) / "docker.log"
        self.run_count = Path(self.temp_dir.name) / "run-count"
        self.sleep_log = Path(self.temp_dir.name) / "sleep.log"
        self.systemctl_log = Path(self.temp_dir.name) / "systemctl.log"
        self.clock = Path(self.temp_dir.name) / "clock"
        self.stress_log = Path(self.temp_dir.name) / "stress.log"
        self._write_command(
            "date",
            "#!/bin/sh\n"
            "if [ \"$1\" = '+%s%N' ]; then cat \"$CLOCK_FILE\"; "
            "else printf '1970-01-01T00:00:00Z\\n'; fi\n",
        )
        self._write_command(
            "sleep",
            "#!/usr/bin/env python3\n"
            "from decimal import Decimal\n"
            "from pathlib import Path\n"
            "import os, sys\n"
            "clock = Path(os.environ['CLOCK_FILE'])\n"
            "delay = int(Decimal(sys.argv[1]) * 1000000000)\n"
            "clock.write_text(str(int(clock.read_text()) + delay + "
            "int(os.environ.get('SLEEP_EXTRA_NS', '0'))))\n"
            "with open(os.environ['SLEEP_LOG'], 'a') as log: print(sys.argv[1], file=log)\n",
        )
        self._write_command(
            "stress-ng",
            "#!/usr/bin/env python3\n"
            "from pathlib import Path\n"
            "import os, sys\n"
            "count_file = Path(os.environ['RUN_COUNT_FILE'])\n"
            "count = int(count_file.read_text()) + 1 if count_file.exists() else 1\n"
            "count_file.write_text(str(count))\n"
            "with open(os.environ['STRESS_LOG'], 'a') as log: print(' '.join(sys.argv[1:]), file=log)\n"
            "if count > 6: sys.exit(23)\n"
            "duration = int(sys.argv[sys.argv.index('--timeout') + 1][:-1])\n"
            "clock = Path(os.environ['CLOCK_FILE'])\n"
            "clock.write_text(str(int(clock.read_text()) + duration * 1000000000 + "
            "int(os.environ.get('STRESS_EXTRA_NS', '0'))))\n",
        )
        self._write_command(
            "docker",
            "#!/bin/sh\n"
            "printf '%s\\n' \"$*\" >> \"$DOCKER_LOG\"\n"
            "if [ \"$1\" = ps ]; then\n"
            "  printf 'container-id\\n'\n"
            "fi\n",
        )
        self._write_command(
            "systemctl",
            "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$SYSTEMCTL_LOG\"\n",
        )
        self.env = os.environ.copy()
        self.env.update(
            {
                "PATH": f"{self.bin_dir}:{os.environ['PATH']}",
                "DOCKER_LOG": str(self.docker_log),
                "RUN_COUNT_FILE": str(self.run_count),
                "SLEEP_LOG": str(self.sleep_log),
                "SYSTEMCTL_LOG": str(self.systemctl_log),
                "CLOCK_FILE": str(self.clock),
                "STRESS_LOG": str(self.stress_log),
                "CPU_HEALTH_SCHEDULE_EPOCH": "1000000",
                "CPU_HEALTH_LOCK_DIR": str(Path(self.temp_dir.name) / "lock"),
            }
        )

    def tearDown(self):
        self.temp_dir.cleanup()

    def _write_command(self, name, contents):
        path = self.bin_dir / name
        path.write_text(contents)
        path.chmod(0o755)

    # C6, C7
    def test_C6_C7_starts_one_unlimited_schedule_container(self):
        self.clock.write_text("999980000000000")
        result = subprocess.run(
            ["bash", str(ROOT / "stress/run.sh"), "start"],
            cwd=ROOT,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=20,
        )

        calls = self.docker_log.read_text().splitlines()
        run_calls = [call for call in calls if call.startswith("run ")]
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(run_calls), 1)
        self.assertIn("--privileged", run_calls[0])
        self.assertIn("CPU_HEALTH_SCHEDULE_EPOCH=1000000", run_calls[0])
        self.assertIn("/usr/local/sbin/cpu-health-stress schedule", run_calls[0])
        self.assertNotIn("--cpus", run_calls[0])
        self.assertNotIn("--cpu-quota", run_calls[0])

    # C6
    def test_schedule_aligns_progressive_bursts_despite_different_start_times(self):
        cases = [
            (-20, [0, 420, 900, 1440, 2040, 2700], [60, 120, 180, 240, 300, 360]),
            (-1, [0, 420, 900, 1440, 2040, 2700], [60, 120, 180, 240, 300, 360]),
            (70, [420, 900, 1440, 2040, 2700, 3420], [120, 180, 240, 300, 360, 60]),
            (360, [420, 900, 1440, 2040, 2700, 3420], [120, 180, 240, 300, 360, 60]),
        ]
        for offset, start_offsets, durations in cases:
            with self.subTest(offset=offset):
                for path in (self.run_count, self.sleep_log, self.stress_log):
                    if path.exists():
                        path.unlink()
                self.clock.write_text(str((1000000 + offset) * 1000000000))
                result = subprocess.run(
                    ["bash", str(ROOT / "stress/run.sh"), "schedule"],
                    cwd=ROOT,
                    env=self.env,
                    capture_output=True,
                    text=True,
                    timeout=20,
                )
                self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
                starts = [line for line in result.stdout.splitlines() if "SYNC_START " in line]
                stops = [line for line in result.stdout.splitlines() if "SYNC_STOP " in line]
                expected_starts = [1000000 + start for start in start_offsets]
                self.assertEqual(
                    [int(line.split("actual_ns=")[1].split()[0]) for line in starts[:6]],
                    [start * 1000000000 for start in expected_starts],
                )
                self.assertEqual(
                    [int(line.split("actual_ns=")[1].split()[0]) for line in stops],
                    [(start + duration) * 1000000000
                     for start, duration in zip(expected_starts, durations)],
                )
                calls = self.stress_log.read_text().splitlines()
                self.assertEqual(
                    [int(call.split("--timeout ")[1].split("s")[0]) for call in calls[:6]],
                    durations,
                )
                self.assertTrue(all("--cpu 0" in call and "--cpu-load 100" in call for call in calls))

    def test_schedule_rejects_a_start_more_than_one_second_late(self):
        self.clock.write_text("999990000000000")
        env = self.env.copy()
        env["SLEEP_EXTRA_NS"] = "1000000001"
        result = subprocess.run(
            ["bash", str(ROOT / "stress/run.sh"), "schedule"],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            timeout=10,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missed synchronized start", result.stdout)
        self.assertFalse(self.stress_log.exists())

    def test_schedule_rejects_a_stop_more_than_one_second_late(self):
        self.clock.write_text("999990000000000")
        env = self.env.copy()
        env["STRESS_EXTRA_NS"] = "1000000001"
        result = subprocess.run(
            ["bash", str(ROOT / "stress/run.sh"), "schedule"],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            timeout=10,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missed synchronized stop", result.stdout)
        self.assertEqual(len(self.stress_log.read_text().splitlines()), 1)

    # C7
    def test_C7_stop_disables_autostart_and_removes_the_stress_container(self):
        result = subprocess.run(
            ["bash", str(ROOT / "stress/run.sh"), "stop"],
            cwd=ROOT,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=5,
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("disable --now cpu-health-stress.service", self.systemctl_log.read_text())
        self.assertIn("rm -f cpu-health-stress", self.docker_log.read_text())

    def test_C7_unknown_command_fails_with_usage(self):
        result = subprocess.run(
            ["bash", str(ROOT / "stress/run.sh"), "restart"],
            cwd=ROOT,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=5,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Usage:", result.stderr)


if __name__ == "__main__":
    unittest.main()
