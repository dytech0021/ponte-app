#!/bin/sh
# Copia o certificado do site para o servidor de mídia do Ponte (que roda sem administrador e não lê o
# /etc/letsencrypt) e o reinicia. Fica em /etc/letsencrypt/renewal-hooks/deploy/, e o certbot o chama a cada
# renovação; rodar à mão também serve.
set -e
origem=/etc/letsencrypt/live/filabox3d.com.br
install -m 640 -o root -g mediamtx "$origem/fullchain.pem" /etc/mediamtx/fullchain.pem
install -m 640 -o root -g mediamtx "$origem/privkey.pem" /etc/mediamtx/privkey.pem
systemctl try-restart mediamtx
