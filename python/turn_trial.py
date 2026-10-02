"""Qt-free turn trial measurement for Rhino Surface Mapper.

Turn trials accumulate signed heading deltas from telemetry across the 000°
boundary. The total is deliberately not folded back into [-180, 180] because a
trial may span more than half a turn. Results are kept in memory and optionally
appended to a semicolon-delimited UTF-8-SIG CSV file for spreadsheet use.
"""
import csv
import math
from datetime import datetime, timezone
from steering import angle_delta
from i18n import translate


class TurnTrial:
    """Record and summarize fixed-duration steering turn trials.

    One instance owns the current trial, completed rows, and optional CSV path.
    Samples are expected to contain a timestamp and heading. A trial is valid
    only when telemetry is fresh at the start, continuous during the command,
    and still fresh when it finishes.
    """

    def __init__(self, path=None):
        """Create a trial recorder, optionally bound to a CSV output path."""
        self.path = path
        self.rows = []
        self.current = None

    def begin(self, direction, now, sample):
        """Start a trial for the given steering direction.

        Args:
            direction: Negative for left, non-negative for right.
            now: Command start time in seconds.
            sample: Latest telemetry sample, or ``None``. A sample older than
                2.5 seconds is ignored so stale headings cannot seed a trial.
        """
        self.current = dict(direction=direction, started=now, baseline=None,
                            last=None, change=0., count=0, gap=False, speeds=[])
        if sample is not None and 0<=now-sample[0]<=2.5:
            self.current['baseline'] = sample[3]
            self.current['last'] = (sample[0], sample[3])

    def observe(self, sample, speed=None):
        """Accumulate one telemetry sample into the active trial.

        Signed heading deltas are added as observed rather than folded into a
        bounded final angle. Gaps over 2.5 seconds mark the trial invalid while
        still preserving the recorded row for diagnostics.
        """
        c = self.current
        if c is None or sample is None or c['last'] is None:
            return
        t,_,_,heading = sample
        previous_t,previous_heading = c['last']
        if t<=previous_t:
            return
        c['gap'] |= t-previous_t>2.5
        c['change'] += angle_delta(previous_heading,heading)
        c['last'] = (t,heading)
        c['count'] += 1
        if speed is not None and math.isfinite(speed):
            c['speeds'].append(speed)

    def finish(self, now, complete=True):
        """Finish the active trial, store a result row, and append CSV if set.

        A valid row requires a complete command, at least one observed delta, no
        telemetry gap over 2.5 seconds, at least 2.5 seconds between command
        start and last sample, and a last sample no older than 2.5 seconds. CSV
        output uses the existing Portuguese column names and ``;`` delimiter for
        compatibility with previously generated files.
        """
        c,self.current = self.current,None
        if c is None:
            return
        valid = bool(complete and c['last'] and c['count'] and not c['gap']
                     and c['last'][0]>=c['started']+2.5 and now-c['last'][0]<=2.5)
        row = dict(utc=datetime.now(timezone.utc).isoformat(),
                   sentido='esquerda' if c['direction']<0 else 'direita',
                   duracao_comando_s=2, rumo_inicial=c['baseline'],
                   rumo_final=c['last'][1] if c['last'] else None,
                   alteracao_assinada_graus=round(c['change'],2),
                   alteracao_absoluta_graus=round(abs(c['change']),2),
                   velocidade_media_estimada_ms=round(sum(c['speeds'])/len(c['speeds']),2) if c['speeds'] else None,
                   amostras=c['count'], valida=valid,
                   motivo='' if valid else ('interrompida' if not complete else 'telemetria insuficiente'))
        self.rows.append(row)
        if self.path:
            self.path.parent.mkdir(parents=True,exist_ok=True)
            header=not self.path.exists()
            with self.path.open('a',encoding='utf-8-sig',newline='') as stream:
                writer=csv.DictWriter(stream,fieldnames=row.keys(),delimiter=';')
                if header:
                    writer.writeheader()
                writer.writerow(row)

    def mean(self, direction=None):
        """Return the mean absolute turn change for valid rows."""
        values=[r['alteracao_absoluta_graus'] for r in self.rows
                if r['valida'] and (direction is None or r['sentido']==direction)]
        return sum(values)/len(values) if values else None

    def summary(self):
        """Return a localized summary of valid turn measurements."""
        value=self.mean()
        count=sum(r['valida'] for r in self.rows)
        if value is None:
            return translate('TurnTrial', 'No turns measured')
        summary = translate('TurnTrial', 'Average {value:.1f}º · {count} turns').format(
            value=value, count=count)
        return summary.replace('.', ',')
