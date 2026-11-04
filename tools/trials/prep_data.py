# Points a trial database at the trial folder: downloads, empty category folders (= subfolders), ffmpeg, no confirmation.
# python prep_data.py <downloads.db> <download folder> [ffmpeg.exe]
import sqlite3, sys
db, folder = sys.argv[1], sys.argv[2]
c = sqlite3.connect(db)
c.execute("insert or replace into settings(key, value) values('DownloadFolder', ?)", (folder,))
# Since 1.4 an empty folder means the download folder itself: a relative folder named after the category (as in a real
# database) is a subfolder of it; the general category stays empty.
c.execute("update categories set folder = name where key <> 'general'")
c.execute("update categories set folder = '' where key = 'general'")
if len(sys.argv) > 3:
    c.execute("insert or replace into settings(key, value) values('FfmpegPath', ?)", (sys.argv[3],))
# Downloads from the trial browsers start directly: no confirmation window on the user's screen.
c.execute("insert or replace into settings(key, value) values('ConfirmBrowserDownloads', '0')")
c.commit()
print(c.execute("select key, value from settings where key in ('DownloadFolder','FfmpegPath','ConfirmBrowserDownloads')").fetchall())
print(c.execute("select name, folder from categories").fetchall(), c.execute("pragma user_version").fetchone())
