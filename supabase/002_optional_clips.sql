-- Update 2: clips become optional.
-- Runs without a clip go on the leaderboard as "unverified". Adding a clip sends the run to the mods ("pending").
-- Run once in Supabase: SQL Editor > New query > paste everything > Run.

-- Drop the old "clip required" and status checks, whatever Postgres named them.
do $$
declare c record;
begin
  for c in select conname from pg_constraint
           where conrelid = 'public.scores'::regclass and contype = 'c'
             and (pg_get_constraintdef(oid) ilike '%clip_url%' or pg_get_constraintdef(oid) ilike '%status%')
  loop
    execute format('alter table public.scores drop constraint %I', c.conname);
  end loop;
end $$;

alter table public.scores add constraint scores_status_check check (status in ('unverified', 'pending', 'verified', 'rejected'));
alter table public.scores alter column status set default 'unverified';

create or replace function public.submit_score(p_code text, p_clip_url text default null, p_clip_path text default null)
returns uuid language plpgsql security definer set search_path = public as $$
declare
  me public.profiles;
  m text[];
  b64 text;
  payload text;
  f text[];
  new_id uuid;
begin
  select * into me from public.profiles where id = auth.uid();
  if me.id is null then raise exception 'Log in first.'; end if;
  if me.banned then raise exception 'This account is banned from submitting scores.'; end if;
  p_clip_url := nullif(trim(coalesce(p_clip_url, '')), '');
  p_clip_path := nullif(trim(coalesce(p_clip_path, '')), '');
  if p_clip_url is not null and p_clip_url !~* '^https://' then raise exception 'Clip links must start with https://'; end if;
  if p_clip_path is not null and split_part(p_clip_path, '/', 1) <> auth.uid()::text then raise exception 'That clip upload is not yours.'; end if;

  m := regexp_match(trim(p_code), '^AIM1\.([A-Za-z0-9_-]+)\.([0-9a-f]{8})$');
  if m is null then raise exception 'That is not a score code. It should start with AIM1.'; end if;
  b64 := translate(m[1], '-_', '+/');
  b64 := b64 || repeat('=', (4 - length(b64) % 4) % 4);
  begin
    payload := convert_from(decode(b64, 'base64'), 'UTF8');
  exception when others then
    raise exception 'That code is damaged. Copy it again from the app.';
  end;
  if public.fnv32('aimtrainer-board-v1-7Qp2xK' || payload) <> m[2] then
    raise exception 'This code does not check out. It was changed or copied wrong.';
  end if;
  f := string_to_array(payload, '|');
  if coalesce(array_length(f, 1), 0) <> 9 or f[1] <> 'v1' or f[4] not in ('T', 'F', 'B', 'R')
     or f[3] not in ('minecraft', 'roblox', 'cs2', 'valorant', 'fortnite', 'overwatch', 'apex', 'custom') then
    raise exception 'This code is from a different version of the app.';
  end if;

  insert into public.scores (user_id, code, ign, game, mode, diff, score, sens, dpi, played_at, clip_url, clip_path, status)
  values (me.id, trim(p_code), left(f[2], 16), f[3], f[4], f[5]::int, f[6]::numeric, f[7]::numeric, f[8]::int,
          to_timestamp(f[9]::bigint), p_clip_url, p_clip_path,
          case when p_clip_url is null and p_clip_path is null then 'unverified' else 'pending' end)
  returning id into new_id;
  return new_id;
exception when unique_violation then
  raise exception 'This code was already submitted.';
end $$;

-- Add a clip to one of your unverified runs to send it for review.
create or replace function public.add_clip(p_id uuid, p_clip_url text default null, p_clip_path text default null)
returns void language plpgsql security definer set search_path = public as $$
declare s public.scores;
begin
  select * into s from public.scores where id = p_id;
  if s.id is null or s.user_id <> auth.uid() then raise exception 'You can only add clips to your own runs.'; end if;
  if exists (select 1 from public.profiles where id = auth.uid() and banned) then raise exception 'This account is banned from submitting scores.'; end if;
  if s.status not in ('unverified') then raise exception 'This run already has a clip.'; end if;
  p_clip_url := nullif(trim(coalesce(p_clip_url, '')), '');
  p_clip_path := nullif(trim(coalesce(p_clip_path, '')), '');
  if p_clip_url is null and p_clip_path is null then raise exception 'Add a clip of the run.'; end if;
  if p_clip_url is not null and p_clip_url !~* '^https://' then raise exception 'Clip links must start with https://'; end if;
  if p_clip_path is not null and split_part(p_clip_path, '/', 1) <> auth.uid()::text then raise exception 'That clip upload is not yours.'; end if;
  update public.scores set clip_url = p_clip_url, clip_path = p_clip_path, status = 'pending' where id = p_id;
end $$;

-- Mods can only verify runs that have a clip.
create or replace function public.review_score(p_id uuid, p_status text)
returns void language plpgsql security definer set search_path = public as $$
declare s public.scores;
begin
  if not public.is_staff() then raise exception 'Only mods and the owner can review scores.'; end if;
  if p_status not in ('pending', 'verified', 'rejected') then raise exception 'Unknown status.'; end if;
  select * into s from public.scores where id = p_id;
  if s.id is null then return; end if;
  if s.clip_url is null and s.clip_path is null then raise exception 'Only runs with a clip can be reviewed.'; end if;
  update public.scores set status = p_status, reviewed_by = auth.uid(), reviewed_at = now() where id = p_id;
end $$;

grant execute on function public.add_clip(uuid, text, text) to authenticated;
