# Fills a trial database with many paused/completed/failed downloads, to measure the list with thousands of rows.
# python fill_downloads.py <downloads.db> <download folder> [count]
import datetime
import sqlite3
import sys

db, folder = sys.argv[1], sys.argv[2]
count = int(sys.argv[3]) if len(sys.argv) > 3 else 2000
c = sqlite3.connect(db)
queue = c.execute("select id from queues order by id limit 1").fetchone()[0]
categories = [row[0] for row in c.execute("select id from categories")]
start = c.execute("select coalesce(max(sort_order), 0) from downloads").fetchone()[0]
now = datetime.datetime.now(datetime.timezone.utc)
rows = []
for i in range(count):
    status = (2, 3, 4)[i % 3]  # paused, completed, failed: nothing starts by itself
    added = (now - datetime.timedelta(minutes=i)).isoformat()
    rows.append((f"http://127.0.0.1:8765/files/perf-{i:05}.bin", folder, f"perf-{i:05}.bin", 1024 * (i + 1), 1, status,
                 "trial" if status == 4 else None, start + i + 1, added, added if status == 3 else None,
                 categories[i % len(categories)], queue))
c.executemany("""insert into downloads(url, directory, file_name, total_size, supports_ranges, status, error, sort_order, added_utc,
                 completed_utc, category_id, queue_id) values (?,?,?,?,?,?,?,?,?,?,?,?)""", rows)
c.commit()
print(c.execute("select count(*) from downloads").fetchone()[0], "downloads")
