from pathlib import Path
import json,sqlite3,hashlib
exec(Path('/tmp/bandcamp-changed-test-setup.py').read_text().split("stage='preflight'")[0])
assert json.loads((ROOT/'restart-result.json').read_text())['changed_value_survived_restart'] and same_working()
owner=api('downloadclient/13');consumer=api('downloadclient/14');indexer=api('indexer/29');assert owner['name']==owner_name and consumer['name']==owner_name+' downloader' and indexer['name']==owner_name+' indexer'
assert not owner['enable'] and not consumer['enable'] and indexer['enableInteractiveSearch'] and not indexer['enableRss'] and not indexer['enableAutomaticSearch']
save('cleanup-dispatch.json',{'disable_manual_search_indexer':29,'clear_only_test_owner_local_credentials':13,'delete_definitions':False,'revoke_bandcamp_session':False})
indexer['enableInteractiveSearch']=False;api('indexer/29?forceSave=true',indexer,'PUT');indexer=api('indexer/29');assert not any(indexer[k] for k in ['enableRss','enableAutomaticSearch','enableInteractiveSearch'])
metadata={'id':13,'name':owner_name,'implementation':'BandcampDownloadClient','configContract':'BandcampDownloadSettings','fields':[]}
old_status=api('downloadclient/action/identityRenewalStatus?operationId=771a1739-660c-4cd8-94ff-4ac371740dba',metadata,'POST');assert old_status['state'].lower()=='unknown';save('operation-status-after-restart.json',old_status)
cleared=api('downloadclient/action/clearLocalCredentials',metadata,'POST');assert cleared.get('success') is True;save('clear-test-local-credentials-receipt.json',cleared)
with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as db:
 settings=json.loads(db.execute('select Settings from DownloadClients where Id=13').fetchone()[0]);assert not any(v for k,v in settings.items() if k.lower() in ['cookies','email','password','verificationcode'])
assert same_working()
for kind,i in [('downloadclient',12),('indexer',26)]:assert api(kind+'/test',api(kind+'/'+str(i)),'POST') in [[],{},None]
files=sorted(Path('/mnt/unraid/media/Music/Great Cynics').rglob('*.flac'));assert len(files)==11
summary={'test_definitions_retained_disabled':[13,14,29],'test_owner_local_credentials_cleared':True,'bandcamp_server_session_not_revoked':True,'working_definitions_unchanged':True,'working_downloader_and_indexer_tests_pass':True,'linked_files':sum(bool(t.get('trackFileId')) for t in api('track?albumId=235835')),'retry_flag':api('config/downloadclient')['autoRedownloadFailed'],'no_download_dispatched':True,'audio_files':[{'relative_path':str(p.relative_to('/mnt/unraid/media/Music/Great Cynics')),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size} for p in files]}
save('cleanup-result.json',summary);print(json.dumps({k:v for k,v in summary.items() if k!='audio_files'}))
