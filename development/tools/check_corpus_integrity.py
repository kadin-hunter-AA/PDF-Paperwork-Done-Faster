"""Verify drift detection on a scratch copy without changing the baseline."""
import sys,shutil,subprocess,json,uuid
from pathlib import Path
source=Path(sys.argv[1]).resolve();scratch=Path(sys.argv[2]).resolve()/('integrity-'+uuid.uuid4().hex)
shutil.copytree(source,scratch)
with (scratch/'engine/original.pdf').open('ab') as file:file.write(b'\n% Deliberate integrity test mutation\n')
result=subprocess.run([sys.executable,str(Path(__file__).parent/'corpus_gate.py'),str(scratch),'verify'],capture_output=True,text=True)
if result.returncode==0 or 'Fixture snapshot changed' not in result.stderr:raise RuntimeError('Corpus integrity gate did not reject altered bytes')
baseline=subprocess.run([sys.executable,str(Path(__file__).parent/'corpus_gate.py'),str(source),'verify'],capture_output=True,text=True)
if baseline.returncode!=0:raise RuntimeError('Baseline changed during integrity test')
(scratch.parent/'corpus-integrity-results.json').write_text(json.dumps({'changedFixtureRejected':True,'baselineStillPasses':True,'scratch':str(scratch)},indent=2))
print('PASS: altered PDF rejected; canonical corpus remains intact.')
