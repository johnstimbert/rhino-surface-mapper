"""Behavioural tests for turn-trial sampling and CSV output.

These tests specify deterministic Qt-free turn measurements used by steering
calibration, including wraparound headings, validity windows, and persistence.
"""

import csv
import tempfile
import unittest
from pathlib import Path
from turn_trial import TurnTrial


class TurnTrialTests(unittest.TestCase):
    """Covers trial summaries built from compact synthetic telemetry samples."""

    def turn(self, trial, direction, baseline, headings, start=0):
        trial.begin(direction,start,(start,0,0,baseline))
        for index,heading in enumerate(headings,1):
            trial.observe((start+index,0,0,heading),10)
        trial.finish(start+7)

    def test_wrap_signed_changes_absolute_means_and_csv(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'results.csv'
            trial=TurnTrial(path)
            self.turn(trial,-1,10,[0,350,340,340,340,340])
            self.turn(trial,1,340,[350,0,20,20,20,20],7)
            self.assertEqual(trial.mean('esquerda'),30)
            self.assertEqual(trial.mean('direita'),40)
            self.assertEqual(trial.mean(),35)
            self.assertEqual(trial.rows[0]['alteracao_assinada_graus'],-30)
            with path.open(encoding='utf-8-sig') as stream:
                rows=list(csv.DictReader(stream,delimiter=';'))
            self.assertEqual(len(rows),2)
            self.assertEqual(rows[1]['velocidade_media_estimada_ms'],'10.0')

    def test_missing_stale_and_interrupted_trials_do_not_enter_mean(self):
        trial=TurnTrial()
        trial.begin(-1,0,None)
        trial.finish(7)
        trial.begin(-1,10,(10,0,0,0))
        trial.observe((11,0,0,10))
        # The last sample is more than 2.5 s before finish, so it is stale.
        trial.finish(17)
        trial.begin(1,20,(20,0,0,0))
        trial.observe((21,0,0,10))
        trial.finish(21,complete=False)
        self.assertIsNone(trial.mean())
        self.assertEqual(len(trial.rows),3)

    def test_repeated_readings_not_counted_and_zero_change_is_valid(self):
        trial=TurnTrial()
        trial.begin(-1,0,(0,0,0,0))
        for t in range(1,7):
            trial.observe((t,0,t,0))
            trial.observe((t,0,t,0))
        trial.finish(7)
        self.assertEqual(trial.rows[0]['amostras'],6)
        self.assertEqual(trial.mean(),0)

    def test_continuous_rotation_larger_than_180_is_not_folded(self):
        trial=TurnTrial()
        self.turn(trial,1,0,[60,120,180,240,240,240])
        self.assertEqual(trial.mean(),240)
