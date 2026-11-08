-- Ponte 1.17.0: só contas liberadas usam o Ponte.
-- Roda no SQL Editor do Supabase do FilaBox (projeto kobtvgqnitdeviddvymm). Pode rodar de novo sem estragar nada.
--
-- A tabela não é lida nem gravada direto por ninguém (RLS sem políticas): tudo passa pelas funções abaixo.
-- O servidor do Ponte chama ponte_conta() com o token de quem pede o bilhete e só assina o bilhete se "liberado".

create table if not exists public.ponte_liberados (
  email        text primary key check (email = lower(email) and position('@' in email) > 1),
  admin        boolean not null default false,
  liberado_em  timestamptz not null default now(),
  liberado_por text
);

alter table public.ponte_liberados enable row level security;
revoke all on public.ponte_liberados from anon, authenticated;

-- O dono é o primeiro liberado e o único administrador da lista.
insert into public.ponte_liberados (email, admin, liberado_por)
values ('dyegofreeride2@gmail.com', true, 'instalação')
on conflict (email) do update set admin = true;

-- Liberado pelo dono em 08/11/2026 (sem ser administrador).
insert into public.ponte_liberados (email, admin, liberado_por)
values ('3dcartola@gmail.com', false, 'dyegofreeride2@gmail.com')
on conflict (email) do nothing;

-- E-mail confirmado da conta que chamou (nulo se não houver conta ou o e-mail não foi confirmado).
create or replace function public.ponte_email()
returns text
language sql
stable
security definer
set search_path = public
as $$
  select lower(u.email)
  from auth.users u
  where u.id = auth.uid()
    and u.email_confirmed_at is not null;
$$;

-- {"email": ..., "liberado": true/false, "admin": true/false} da conta que chamou.
create or replace function public.ponte_conta()
returns json
language sql
stable
security definer
set search_path = public
as $$
  select json_build_object(
    'email', public.ponte_email(),
    'liberado', exists (select 1 from public.ponte_liberados l where l.email = public.ponte_email()),
    'admin', coalesce((select l.admin from public.ponte_liberados l where l.email = public.ponte_email()), false)
  );
$$;

create or replace function public.ponte_admin()
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select coalesce((select l.admin from public.ponte_liberados l where l.email = public.ponte_email()), false);
$$;

-- Lista para o administrador.
create or replace function public.ponte_lista()
returns table (email text, admin boolean, liberado_em timestamptz, liberado_por text)
language plpgsql
stable
security definer
set search_path = public
as $$
begin
  if not public.ponte_admin() then
    raise exception 'Só o administrador do Ponte vê a lista.' using errcode = '42501';
  end if;
  return query select l.email, l.admin, l.liberado_em, l.liberado_por from public.ponte_liberados l order by l.liberado_em;
end;
$$;

create or replace function public.ponte_liberar(p_email text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
  alvo text := lower(trim(p_email));
begin
  if not public.ponte_admin() then
    raise exception 'Só o administrador do Ponte libera contas.' using errcode = '42501';
  end if;
  if alvo !~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' then
    raise exception 'E-mail inválido.' using errcode = '22023';
  end if;
  insert into public.ponte_liberados (email, liberado_por) values (alvo, public.ponte_email())
  on conflict (email) do nothing;
end;
$$;

create or replace function public.ponte_remover(p_email text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
  alvo text := lower(trim(p_email));
begin
  if not public.ponte_admin() then
    raise exception 'Só o administrador do Ponte tira contas da lista.' using errcode = '42501';
  end if;
  if alvo = public.ponte_email() then
    raise exception 'Você não pode tirar a sua própria conta.' using errcode = '22023';
  end if;
  delete from public.ponte_liberados where email = alvo;
end;
$$;

revoke all on function public.ponte_email(), public.ponte_conta(), public.ponte_admin(), public.ponte_lista(),
  public.ponte_liberar(text), public.ponte_remover(text) from public, anon;
grant execute on function public.ponte_conta(), public.ponte_lista(), public.ponte_liberar(text), public.ponte_remover(text) to authenticated;
