from pathlib import Path
import json,os,sqlite3,subprocess,time,hashlib
exec(Path('/tmp/bandcamp-changed-test-setup.py').read_text().split("stage='preflight'")[0])
expected='674d2de854890f625e84d0d3702d0a9cbc3f0860c9479f5eee6f86aa93c26cc4';image='sha256:5618a549ba6ef21318d24f869764fa58f51629b063a1a1ebf11cf8f32d1f5b8a'
def inspect():return json.loads(subprocess.check_output(['docker','inspect','lidarr']))[0]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def test_rows_match():
 with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as live,sqlite3.connect((ROOT/'lidarr.after-repair.db').as_uri()+'?mode=ro',uri=True) as saved:
  return all(live.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone()==saved.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone() for t,i in [('DownloadClients',13),('DownloadClients',14),('Indexers',29)])
stage='preflight'
try:
 state=inspect();assert state['State']['Running'] and state['Image']==image and sha(C/'plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll')==expected
 assert same_working() and test_rows_match()
 assert sha(C/'config.xml')==sha(ROOT/'config.xml') and sha(C.parent/'compose.yaml')==sha(ROOT/'compose.yaml')
 assert not [x for x in api('queue?pageSize=100')['records'] if x.get('downloadClientId') in [12,13,14]]
 assert not [x for x in api('command') if x.get('status') in ['started','queued'] and any(k in x.get('name','').lower() for k in ['import','downloadedalbumsscan'])]
 assert sum(bool(x.get('trackFileId')) for x in api('track?albumId=235835'))==11
 fd=os.open(ROOT/'restart-dispatch.jsonl',os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
 with os.fdopen(fd,'w') as f:
  def record(phase):f.write(json.dumps({'phase':phase,'utc':time.time()})+'\n');f.flush();os.fsync(f.fileno());print('phase='+phase,flush=True)
  record('dispatched')
  for action in ['stop','start']:
   stage=action
   with (ROOT/('restart-'+action+'.log')).open('x') as log:subprocess.run(['docker','compose','-f',str(C.parent/'compose.yaml'),action,'lidarr'],stdout=log,stderr=subprocess.STDOUT,check=True)
   record(action)
  stage='readiness';ready=False
  for _ in range(15):
   try:ready=api('system/status').get('version')=='3.2.1.5196'
   except Exception:pass
   if ready:break
   time.sleep(2)
  final=inspect();assert ready and final['Id']==state['Id'] and final['Image']==image and final['State']['Running'] and sha(C/'plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll')==expected
  assert same_working() and test_rows_match();record('verified_changed_value_loaded')
  stage='post_restart_tests';tests=[]
  for kind,i in [('downloadclient',13),('downloadclient',14),('indexer',29)]:
   assert api(kind+'/test',api(kind+'/'+str(i)),'POST') in [[],{},None];tests.append({'kind':kind,'id':i,'passed':True})
  rs=api('release?albumId=235835');flac=[x for x in rs if x.get('indexerId')==29 and x.get('quality',{}).get('quality',{}).get('name')=='FLAC'];assert len(flac)==1
  result={'changed_value_survived_restart':True,'test_owner_and_consumers_match_post_repair_snapshot':test_rows_match(),'tests':tests,'test_indexer_flac_results':len(flac),'working_definitions_unchanged':same_working(),'linked_files':sum(bool(x.get('trackFileId')) for x in api('track?albumId=235835')),'retry_flag':api('config/downloadclient')['autoRedownloadFailed'],'no_grab':True,'same_container':True,'same_image':True,'same_dll':True}
  assert result['test_owner_and_consumers_match_post_repair_snapshot'] and result['working_definitions_unchanged'] and result['linked_files']==11 and result['retry_flag'];save('restart-result.json',result);print(json.dumps(result))
except Exception as e:print(json.dumps({'result':'STOP','stage':stage,'error_type':type(e).__name__,'no_automatic_inverse':True}));raise SystemExit(1)
