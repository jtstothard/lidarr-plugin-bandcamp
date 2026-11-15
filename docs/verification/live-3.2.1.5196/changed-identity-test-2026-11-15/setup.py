import json,os,copy,hashlib,sqlite3,urllib.request,urllib.error,xml.etree.ElementTree as E
from pathlib import Path
ROOT=Path('/srv/docker-state/rollback-preparation/bandcamp-changed-identity-test-20261115T180222Z');C=Path('/srv/docker-state/stacks/media/lidarr');owner_name='Bandcamp changed-identity qualification 20261115';bad='identity=qualification-invalid-20261115-changed-value'
cfg=E.parse(C/'config.xml').getroot();base='http://192.168.10.252:'+cfg.findtext('Port')+(cfg.findtext('UrlBase') or '')+'/api/v1/'
def api(path,data=None,method=None):
 request=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers={'X-Api-Key':cfg.findtext('ApiKey'),'Content-Type':'application/json'},method=method)
 with urllib.request.urlopen(request,timeout=45) as r:return json.load(r)
def save(name,value):
 fd=os.open(ROOT/name,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
 with os.fdopen(fd,'w') as f:json.dump(value,f,indent=2)
def same_working():
 with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as live,sqlite3.connect((ROOT/'lidarr.db').as_uri()+'?mode=ro',uri=True) as old:
  return all(live.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone()==old.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone() for t,i in [('DownloadClients',12),('Indexers',26)])
def disabled_clone(dto,name,auth,cookie,path=None):
 d=copy.deepcopy(dto);d.pop('id',None);d['name']=name;d['enable']=False
 for key in ['enableRss','enableAutomaticSearch','enableInteractiveSearch']: 
  if key in d:d[key]=False
 d['tags']=[]
 values={'email':'','password':'','verificationCode':'','cookies':cookie,'authConfiguration':auth}
 if path:values['downloadPath']=path
 for f in d['fields']:
  if f['name'] in values:f['value']=values[f['name']]
 return d
stage='preflight';created=[]
try:
 assert same_working();assert hashlib.sha256((C/'plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll').read_bytes()).hexdigest()=='674d2de854890f625e84d0d3702d0a9cbc3f0860c9479f5eee6f86aa93c26cc4'
 existing=api('downloadclient')+api('indexer');assert not any(x.get('name','').startswith(owner_name) for x in existing)
 client=api('downloadclient/12');indexer=api('indexer/26');rootpath='/data/downloads/bandcamp-qualification-20261115-changed-value'
 for kind,role,dto in [('downloadclient','owner',disabled_clone(client,owner_name,'',bad,rootpath+'/owner')),('downloadclient','consumer',disabled_clone(client,owner_name+' downloader',owner_name,'',rootpath+'/consumer')),('indexer','indexer',disabled_clone(indexer,owner_name+' indexer',owner_name,''))]:
  stage='create_'+role;save(role+'-create-dispatch.json',{'kind':kind,'name':dto['name'],'disabled':True})
  r=api(kind,dto,'POST');created.append({'role':role,'kind':kind,'id':r['id'],'name':r['name']});save(role+'-created.json',created[-1])
 save('test-definitions.json',created)
 # Only the new disabled fixtures are tested. Never call password login.
 results=[]
 for item in [x for x in created if x['role']!='owner']:
  stage='red_'+item['role'];dto=api(item['kind']+'/'+str(item['id']))
  try:
   api(item['kind']+'/test',dto,'POST');result={'role':item['role'],'http_status':200,'failed_as_expected':False}
  except urllib.error.HTTPError as e:
   body=json.load(e);result={'role':item['role'],'http_status':e.code,'failed_as_expected':e.code==400,'validation_properties':[x.get('propertyName') for x in body] if isinstance(body,list) else []}
  results.append(result)
 save('red-results.json',results);assert all(x['failed_as_expected'] for x in results)
 with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as db:
  row=db.execute('select Settings from DownloadClients where Id=?',(created[0]['id'],)).fetchone()[0];save('test-owner-before-settings.json',json.loads(row))
 assert same_working();print(json.dumps({'created':created,'red_results':results,'working_definitions_unchanged':True}))
except Exception as e:
 print(json.dumps({'result':'STOP','stage':stage,'error_type':type(e).__name__,'created_ids':created,'no_automatic_retry':True}));raise SystemExit(1)
