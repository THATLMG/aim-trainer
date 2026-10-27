// LMG's Aim Trainer for Mac: shows the browser game (docs/play.html) in a native window
// and sets the player's Discord status through Discord's local connection.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use discord_rich_presence::{activity, DiscordIpc, DiscordIpcClient};
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};
use tauri::Manager;
use std::time::{SystemTime, UNIX_EPOCH};

const DISCORD_APP_ID: &str = "1564084223093121065";
const SITE: &str = "https://thatlmg.github.io/aim-trainer/";

static STARTED: OnceLock<i64> = OnceLock::new();

struct Discord(Mutex<Option<DiscordIpcClient>>);

#[tauri::command]
fn set_presence(discord: tauri::State<Discord>, details: String, status: String, game: String, invite: Option<String>) -> Result<(), String> {
    let mut slot = discord.0.lock().map_err(|e| e.to_string())?;
    if slot.is_none() {
        // Connect lazily; this fails quietly when Discord isn't running and the page tries again later.
        let mut client = DiscordIpcClient::new(DISCORD_APP_ID).map_err(|e| e.to_string())?;
        client.connect().map_err(|e| e.to_string())?;
        *slot = Some(client);
    }

    let logo = format!("{SITE}rpc/logo.png");
    let badge = format!("{SITE}rpc/{game}.png");
    let leaderboard = format!("{SITE}board.html");
    let mut assets = activity::Assets::new().large_image(&logo).large_text("LMG's Aim Trainer");
    if !game.is_empty() {
        assets = assets.small_image(&badge).small_text(&game);
    }
    let mut act = activity::Activity::new()
        .details(&details)
        .assets(assets)
        .timestamps(activity::Timestamps::new().start(*STARTED.get().unwrap_or(&0)))
        .buttons(vec![
            activity::Button::new("Get LMG's Aim Trainer", SITE),
            // Once the Discord server exists, its invite replaces the leaderboard button.
            match invite.as_deref() {
                Some(url) if url.starts_with("https://discord.") => activity::Button::new("Join the Discord", url),
                _ => activity::Button::new("Leaderboard", &leaderboard),
            },
        ]);
    if status.chars().count() >= 2 {
        act = act.state(&status);
    }

    let client = slot.as_mut().expect("connected above");
    if let Err(e) = client.set_activity(act) {
        *slot = None; // Discord was closed; reconnect next time
        return Err(e.to_string());
    }
    Ok(())
}

// A copy of the page's saved data (name, settings, ranks, unlocks) in the app's own folder,
// so it survives even if the window's storage is cleared when the app is updated.
fn backup_path(app: &tauri::AppHandle) -> Result<PathBuf, String> {
    let dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir.join("saved-data.json"))
}

#[tauri::command]
fn save_backup(app: tauri::AppHandle, data: String) -> Result<(), String> {
    let path = backup_path(&app)?;
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, data).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, &path).map_err(|e| e.to_string())
}

#[tauri::command]
fn load_backup(app: tauri::AppHandle) -> Result<String, String> {
    Ok(std::fs::read_to_string(backup_path(&app)?).unwrap_or_default())
}

fn main() {
    let now = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    let _ = STARTED.set(now);

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(Discord(Mutex::new(None)))
        .invoke_handler(tauri::generate_handler![set_presence, save_backup, load_backup])
        .run(tauri::generate_context!())
        .expect("error while running LMG's Aim Trainer");
}
