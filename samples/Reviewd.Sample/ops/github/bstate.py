import sqlite3,json
c=sqlite3.connect('/mnt/b/published/Reviewd/state/reviews.db',timeout=10)
for k,v in c.execute("select key,value from cache where key like 'reviewd/batch-state/%'"):
    d=json.loads(v)
    for e in d.get('entries',[]):
        if e.get('status') not in ('verified',): print(k[-8:], {x:e.get(x) for x in ('repository','prId','slot','status','reason','retryAt','heldAt','attempts') if e.get(x) is not None})
