from pathlib import Path
import json,os,sqlite3
# Reuse the prepared authenticated local-admin transport; no server apphost invocation.
exec(Path('/tmp/bandcamp-changed-test-setup.py').read_text().split("stage='preflight'")[0])
metadata={'id':13,'name':owner_name,'implementation':'BandcampDownloadClient','configContract':'BandcampDownloadSettings','fields':[]}
stage='preflight';operation=None
try:
 assert same_working()
 definitions=json.loads((ROOT/'test-definitions.json').read_text());assert {x['role']:x['id'] for x in definitions}=={'owner':13,'consumer':14,'indexer':29}
 with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as db:
  row=db.execute('select Name,Settings from DownloadClients where Id=13').fetchone();assert row[0]==owner_name;stored=json.loads(row[1]);assert stored['cookies']==bad
  working=json.loads(db.execute('select Settings from DownloadClients where Id=12').fetchone()[0]);candidate=next(v for k,v in working.items() if k.lower()=='cookies')
 assert candidate and candidate!=bad
 save('api-helper-repair-dispatch.json',{'owner_id':13,'expected_fan_id':67857,'maximum_candidate_submissions':1})
 stage='begin';begun=api('downloadclient/action/beginIdentityRenewal',metadata,'POST');save('begin-receipt.json',begun);operation=begun['operationId'];assert begun['state'].lower()=='pending'
 body=dict(metadata);body['fields']=[{'name':'cookies','value':candidate}]
 stage='renew';result=api('downloadclient/action/renewIdentity?fanId=67857&operationId='+operation,body,'POST');save('renew-receipt.json',result)
 stage='status';status=api('downloadclient/action/identityRenewalStatus?operationId='+operation,metadata,'POST');save('status-receipt.json',status)
 assert result['state'].lower()=='committed' and status['state'].lower()=='committed'
 with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as db:
  current=json.loads(db.execute('select Settings from DownloadClients where Id=13').fetchone()[0]);assert current['cookies']!=bad and current['cookies']==candidate
  prior=json.loads((ROOT/'consumer-rows-before-repair.json').read_text());same_consumers=all(list(db.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone())==prior[t] for t,i in [('DownloadClients',14),('Indexers',29)])
 assert same_consumers and same_working()
 tests=[]
 for kind,i in [('downloadclient',13),('downloadclient',14),('indexer',29)]:
  response=api(kind+'/test',api(kind+'/'+str(i)),'POST');assert response in [[],{},None];tests.append({'kind':kind,'id':i,'passed':True})
 summary={'operation_id':operation,'committed':True,'changed_value_persisted':True,'test_owner_matches_valid_working_identity':True,'consumer_settings_unchanged':True,'working_definitions_unchanged':same_working(),'tests':tests,'transport':'same native helper protocol over existing SSH/local-admin route; not a second desktop HTTPS run'}
 save('repair-result.json',summary);print(json.dumps(summary))
except Exception as e:
 # If an ID exists, reconcile it before another effect; never resubmit candidate.
 if operation:
  try:save('failure-status-receipt.json',api('downloadclient/action/identityRenewalStatus?operationId='+operation,metadata,'POST'))
  except Exception:pass
 print(json.dumps({'result':'STOP','stage':stage,'error_type':type(e).__name__,'operation_id':operation,'candidate_not_retried':True}));raise SystemExit(1)
