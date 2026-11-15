from pathlib import Path
import json,os,sqlite3,copy
exec(Path('/tmp/bandcamp-changed-test-setup.py').read_text().split("stage='preflight'")[0])
assert json.loads((ROOT/'repair-result.json').read_text())['changed_value_persisted']
dto=api('indexer/29');assert dto['name']==owner_name+' indexer' and not dto['enableRss'] and not dto['enableAutomaticSearch'] and not dto['enableInteractiveSearch']
save('indexer-before-manual-search.json',dto);save('manual-search-enable-dispatch.json',{'indexer_id':29,'enableRss':False,'enableAutomaticSearch':False,'enableInteractiveSearch':True})
dto['enableInteractiveSearch']=True;api('indexer/29?forceSave=true',dto,'PUT');current=api('indexer/29');assert current['enableInteractiveSearch'] and not current['enableRss'] and not current['enableAutomaticSearch']
rs=api('release?albumId=235835');selected=[x for x in rs if x.get('indexerId')==29 and x.get('quality',{}).get('quality',{}).get('name')=='FLAC'];assert len(selected)==1
save('pre-restart-search.json',{'indexer_id':29,'flac_results':len(selected),'approved':selected[0].get('approved'),'no_grab':True})
assert same_working()
# Retain an independent online snapshot after the changed value has been committed.
with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as source,sqlite3.connect(ROOT/'lidarr.after-repair.db') as target:
 source.backup(target,pages=256,sleep=0.1);assert target.execute('pragma integrity_check').fetchall()==[('ok',)]
print(json.dumps({'manual_only_test_indexer':29,'flac_results':1,'automatic_search':False,'rss':False,'no_grab':True,'working_definitions_unchanged':True,'post_repair_snapshot_integrity':'ok'}))
