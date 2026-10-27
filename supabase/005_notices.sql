-- Update 5: notices from the owner to every player.
-- Only the PC with the owner key file (%APPDATA%\AimTrainer\owner.key) can send one. The database keeps just a
-- SHA-256 hash of that key, so the key itself never leaves that PC.
-- Run once in Supabase: SQL Editor > New query > paste everything > Run.

create table if not exists public.notices (
  id bigint generated always as identity primary key,
  text text not null check (char_length(text) between 1 and 300),
  important boolean not null default false,
  discord_posted boolean not null default false,
  created_at timestamptz not null default now()
);
alter table public.notices enable row level security;
drop policy if exists "anyone can read notices" on public.notices;
create policy "anyone can read notices" on public.notices for select using (true);

-- Hashes of keys that may send notices. No policies: nobody can read or change this from outside.
create table if not exists public.owner_keys (key_hash text primary key);
alter table public.owner_keys enable row level security;
insert into public.owner_keys (key_hash) values ('dc37e4356de228d9c9f9ab885c1c0bb7892711473a2c863f96c810ef7e58a83e')
on conflict do nothing;

create or replace function public.send_notice(p_key text, p_text text, p_important boolean default false)
returns bigint language plpgsql security definer set search_path = public as $$
declare new_id bigint;
begin
  if not exists (select 1 from public.owner_keys where key_hash = encode(sha256(convert_to(coalesce(p_key, ''), 'UTF8')), 'hex')) then
    raise exception 'Not allowed to send notices.';
  end if;
  p_text := trim(coalesce(p_text, ''));
  if char_length(p_text) = 0 then raise exception 'Type a message first.'; end if;
  if char_length(p_text) > 300 then raise exception 'Keep it under 300 characters.'; end if;
  insert into public.notices (text, important) values (p_text, coalesce(p_important, false)) returning id into new_id;
  return new_id;
end $$;
grant execute on function public.send_notice(text, text, boolean) to anon, authenticated;

-- Also post each notice to Discord through the bot (does nothing until the bot is set up).
create extension if not exists pg_net with schema extensions;
create or replace function public.notify_discord_notice() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  perform net.http_post(
    url := 'https://lzigsxqthaqzlanvygmm.supabase.co/functions/v1/discord-bot?event=notice',
    body := jsonb_build_object('id', new.id),
    headers := '{"Content-Type": "application/json"}'::jsonb);
  return new;
end $$;
drop trigger if exists notices_notify_discord on public.notices;
create trigger notices_notify_discord after insert on public.notices
  for each row execute function public.notify_discord_notice();
