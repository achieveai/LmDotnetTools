import sqlite3,json
c=sqlite3.connect('/mnt/b/published/Reviewd/state/reviews.db',timeout=10)
for k,v in sorted(c.execute("select key,value from cache where key like 'reviewd/review-attempt/%'")):
    d=json.loads(v); print(k.split('/',3)[3].ljust(42), d.get('stage'), 'slot', d.get('slot'), (d.get('error') or '')[:200].replace('\n',' '))
