-- Update 6: reviews.
-- Anyone can rate the app from 1 to 10 on the website (reviews.html). Everyone sees the average and how the ratings
-- spread out; only the owner sees what people wrote. A rating of 4 or below needs a reason, and is sent to the owner:
-- in the aim trainer on the PC with the owner key, and in Discord through the bot (once it's set up).
-- Run once in Supabase: SQL Editor > New query > paste everything > Run. Needs 005_notices.sql first (owner key).

create table if not exists public.reviews (
  id bigint generated always as identity primary key,
  device text not null unique check (char_length(device) between 16 and 64),   -- random id kept by the browser: one review each, which they can change
  name text not null default '' check (char_length(name) <= 24),
  rating int not null check (rating between 1 and 10),
  text text not null default '' check (char_length(text) <= 600),
  platform text not null default '' check (char_length(platform) <= 20),
  discord_posted boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
alter table public.reviews enable row level security;
-- Nobody reads the table directly except the owner signed in on the website. Everyone else goes through the functions below.
drop policy if exists "owner reads reviews" on public.reviews;
create policy "owner reads reviews" on public.reviews for select using (coalesce(public.my_role(), '') = 'owner');

create or replace function public.submit_review(p_device text, p_name text, p_rating int, p_text text, p_platform text default '')
returns bigint language plpgsql security definer set search_path = public as $$
declare new_id bigint;
begin
  p_text := trim(coalesce(p_text, ''));
  p_name := left(trim(coalesce(p_name, '')), 24);
  p_platform := left(coalesce(p_platform, ''), 20);
  if p_device is null or char_length(p_device) not between 16 and 64 then raise exception 'Reload the page and try again.'; end if;
  if p_rating is null or p_rating not between 1 and 10 then raise exception 'Pick a rating from 1 to 10.'; end if;
  if p_rating <= 4 and char_length(p_text) < 5 then raise exception 'Tell us what''s wrong so it can be fixed.'; end if;
  if char_length(p_text) > 600 then raise exception 'Keep it under 600 characters.'; end if;
  if exists (select 1 from public.reviews where device = p_device and updated_at > now() - interval '30 seconds') then
    raise exception 'You just sent one. Wait a moment before changing it.';
  end if;
  insert into public.reviews (device, name, rating, text, platform) values (p_device, p_name, p_rating, p_text, p_platform)
  on conflict (device) do update set
    name = excluded.name, rating = excluded.rating, text = excluded.text, platform = excluded.platform, updated_at = now(),
    -- a changed low review is sent to the owner again
    discord_posted = case when reviews.rating is distinct from excluded.rating or reviews.text is distinct from excluded.text then false else reviews.discord_posted end
  returning id into new_id;
  return new_id;
end $$;

-- The public numbers: how many reviews, the average, and how many of each rating (1 to 10).
create or replace function public.review_summary()
returns jsonb language sql stable security definer set search_path = public as $$
  select jsonb_build_object(
    'count', count(*),
    'average', coalesce(round(avg(rating)::numeric, 1), 0),
    'spread', (select jsonb_agg(coalesce((select count(*) from public.reviews r where r.rating = s.n), 0) order by s.n) from generate_series(1, 10) as s(n)))
  from public.reviews
$$;

-- Your own review, so the page can show it and let you change it.
create or replace function public.my_review(p_device text)
returns table (name text, rating int, text text, updated_at timestamptz)
language sql stable security definer set search_path = public as $$
  select name, rating, text, updated_at from public.reviews where device = p_device
$$;

-- For the owner's aim trainer: reviews at or below a rating, changed after a time, newest first.
create or replace function public.owner_reviews(p_key text, p_max_rating int default 10, p_since timestamptz default '1970-01-01')
returns table (id bigint, name text, rating int, text text, platform text, updated_at timestamptz)
language plpgsql stable security definer set search_path = public as $$
begin
  if not exists (select 1 from public.owner_keys where key_hash = encode(sha256(convert_to(coalesce(p_key, ''), 'UTF8')), 'hex')) then
    raise exception 'Not allowed.';
  end if;
  return query select r.id, r.name, r.rating, r.text, r.platform, r.updated_at from public.reviews r
    where r.rating <= coalesce(p_max_rating, 10) and r.updated_at > coalesce(p_since, '1970-01-01')
    order by r.updated_at desc limit 200;
end $$;

grant execute on function public.submit_review(text, text, int, text, text), public.review_summary(), public.my_review(text),
  public.owner_reviews(text, int, timestamptz) to anon, authenticated;

-- Low reviews (4 and below) are also posted to the owner's private Discord channel by the bot.
create extension if not exists pg_net with schema extensions;
create or replace function public.notify_discord_review() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if new.rating <= 4 and not new.discord_posted then
    perform net.http_post(
      url := 'https://lzigsxqthaqzlanvygmm.supabase.co/functions/v1/discord-bot?event=review',
      body := jsonb_build_object('id', new.id),
      headers := '{"Content-Type": "application/json"}'::jsonb);
  end if;
  return new;
end $$;
drop trigger if exists reviews_notify_discord on public.reviews;
create trigger reviews_notify_discord after insert or update on public.reviews
  for each row execute function public.notify_discord_review();
