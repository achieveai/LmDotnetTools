import sqlite3,json
c=sqlite3.connect('/mnt/b/published/Reviewd/state/reviews.db',timeout=10)
for pr in ('agentworkbench/36','lmdotnettools/803'):
    for kind in ('review-run','review-attempt'):
        r=c.execute('select value from cache where key=?',(f'reviewd/{kind}/github/achieveai/{pr}',)).fetchone()
        d=json.loads(r[0]) if r else {}
        print(kind,pr,d.get('stage'),'head',(d.get('headSha') or '')[:7],'thread',d.get('threadId'))
