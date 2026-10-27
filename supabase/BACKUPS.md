# Database backups

`.github/workflows/backup.yml` saves a copy of the whole Supabase database every 2 days:
- accounts and profiles;
- scores;
- notices;
- reviews;
- all the tables, rules and functions.

Each copy is encrypted and kept for 90 days under **Actions > Database backup** on GitHub.

It does **not** include uploaded clip files (Supabase Storage).

## One-time setup

1. **Get the connection string.**
   - In Supabase, click **Connect** (top of the project).
   - Choose **Session pooler** and copy the URI.
   - Replace `[YOUR-PASSWORD]` with your database password. If you don't know it, reset it in **Project Settings > Database**.
2. **Make up a backup passphrase.** Use a long one, and save it in your password manager. Without it, the backups can't be opened, even by you.
3. **Add both as repo secrets.** On GitHub, open the repo, then **Settings > Secrets and variables > Actions > New repository secret**:
   - `SUPABASE_DB_URL`: the connection string from step 1.
   - `BACKUP_PASSPHRASE`: the passphrase from step 2.
4. **Run the first backup.** Go to **Actions > Database backup > Run workflow**. It should finish green in a minute or two.

Never paste the connection string or passphrase into chat or into any file in this repo.

GitHub pauses scheduled workflows in repos with no commits for 60 days. If that happens, the Actions tab shows a button to turn it back on.

## Restoring

1. **Download the backup.** Open the run you want under **Actions > Database backup** and download its artifact. It's a zip containing `aim-trainer-db-DATE.tar.gz.gpg`.
2. **Decrypt and unpack it.** Use Git Bash on Windows (it includes `gpg` and `tar`):
   ```bash
   gpg --decrypt -o backup.tar.gz aim-trainer-db-DATE.tar.gz.gpg
   ```
   ```bash
   tar -xzf backup.tar.gz
   ```
   This gives you `roles.sql`, `schema.sql` and `data.sql`.
3. **Load it.** To load it into a fresh Supabase project, install `psql` and run:
   ```bash
   psql --single-transaction --variable ON_ERROR_STOP=1 --file roles.sql --file schema.sql --command 'SET session_replication_role = replica' --file data.sql --dbname "NEW_PROJECT_CONNECTION_STRING"
   ```

To get back a single table or row, open `data.sql` in a text editor. It's plain text.
