-- Update 4: the Discord bot.
-- When a run is sent for review, approved or rejected, the database tells the bot (supabase/functions/discord-bot),
-- which posts it in Discord. Run once in Supabase: SQL Editor > New query > paste everything > Run.
-- Deploy the bot function first (see discord/SETUP.md), or the calls just fail quietly until it exists.

-- What the bot has already posted, so nothing is posted twice. Only the bot (service key) can read or write it.
create table if not exists public.discord_posts (
  score_id uuid not null references public.scores on delete cascade,
  kind text not null,              -- 'review', 'review-closed' or 'verified'
  channel_id text,
  message_id text,
  created_at timestamptz not null default now(),
  primary key (score_id, kind)
);
alter table public.discord_posts enable row level security;

create extension if not exists pg_net with schema extensions;

-- Sends only the run's id; the bot reads the run itself, so nobody can make it post something untrue.
create or replace function public.notify_discord() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if new.status in ('pending', 'verified', 'rejected') and (tg_op = 'INSERT' or new.status is distinct from old.status) then
    perform net.http_post(
      url := 'https://lzigsxqthaqzlanvygmm.supabase.co/functions/v1/discord-bot?event=score',
      body := jsonb_build_object('id', new.id),
      headers := '{"Content-Type": "application/json"}'::jsonb);
  end if;
  return new;
end $$;

drop trigger if exists scores_notify_discord on public.scores;
create trigger scores_notify_discord after insert or update of status on public.scores
  for each row execute function public.notify_discord();
