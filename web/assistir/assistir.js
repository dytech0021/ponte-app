// Ponte · assistir a uma transmissão de tela. O código da transmissão vem depois do # no link (essa parte o
// navegador não manda ao servidor). O vídeo chega por WebRTC (WHEP) do servidor de mídia, com menos de 1 s de
// atraso. Se o WebRTC não passar pela rede de quem assiste, cai para HLS (alguns segundos de atraso) nos
// navegadores que tocam HLS sozinhos (iPhone, iPad, Safari, Android).

const $ = (id) => document.getElementById(id);
const video = $('video');
const token = decodeURIComponent(location.hash.slice(1)).trim();
const whepUrl = new URL(`../ao-vivo/ao/${token}/whep`, location.href).href;
const hlsUrl = new URL(`../ao-vivo-hls/ao/${token}/index.m3u8`, location.href).href;

// Dentro do aplicativo do Ponte (Android), o som pode começar ligado. Do app 1.8.3 em diante, a página pede a ele a
// tela cheia (com ou sem as barras do Android) e a janela flutuante, pelo objeto PonteApp.
const inApp = / PonteApp\//.test(navigator.userAgent);
const app = inApp ? window.PonteApp : undefined;
if (inApp) {
  video.muted = false;
  $('som').textContent = 'Som ligado';
  $('som').classList.add('ligado');
  if (!app) $('tela-cheia').hidden = true;   // os apps anteriores ficam sempre em tela cheia
} else if (/Android/i.test(navigator.userAgent) && /^[A-Za-z0-9]{24}$/.test(token)) {
  // No navegador de um Android: quem tem o app do Ponte assiste nele; quem não tem cai na página de baixar.
  const fallback = encodeURIComponent(new URL(`../baixar/#${token}`, location.href).href);
  $('app').href = `intent://assistir/${token}#Intent;scheme=ponte;package=com.dytech.ponte;S.browser_fallback_url=${fallback};end`;
  $('app').hidden = false;
}

let pc = null;
let session = null;
let failures = 0;
let retryTimer = 0;
let disconnectTimer = 0;
let usingHls = false;
let ended = false;
let wakeLock = null;

function say(text) {
  $('aviso-texto').textContent = text;
  $('aviso').hidden = !text;
}

function setLive(live, label) {
  $('ponto').classList.toggle('vivo', live);
  $('rotulo').textContent = label;
  if (live) navigator.wakeLock?.request('screen').then((lock) => { wakeLock = lock; }).catch(() => {});
}

function closeSession() {
  clearTimeout(disconnectTimer);
  if (session) {
    fetch(session, { method: 'DELETE', keepalive: true }).catch(() => {});
    session = null;
  }
  if (pc) {
    pc.ontrack = pc.onconnectionstatechange = null;
    pc.close();
    pc = null;
  }
}

function retry(text, ms = 3000) {
  closeSession();
  if (ended) return;
  say(text);
  setLive(false, 'Ponte · aguardando');
  clearTimeout(retryTimer);
  retryTimer = setTimeout(connect, ms);
}

function finish(text) {
  ended = true;
  closeSession();
  say(text);
  setLive(false, 'Ponte · encerrada');
}

// Espera o navegador juntar os caminhos de rede possíveis (no máximo 3 s) antes de mandar a oferta.
function gathered(connection) {
  if (connection.iceGatheringState === 'complete') return Promise.resolve();
  return new Promise((resolve) => {
    const done = () => {
      if (connection.iceGatheringState !== 'complete') return;
      connection.removeEventListener('icegatheringstatechange', done);
      resolve();
    };
    connection.addEventListener('icegatheringstatechange', done);
    setTimeout(resolve, 3000);
  });
}

async function connect() {
  if (ended || usingHls) return;
  closeSession();
  const connection = new RTCPeerConnection({ bundlePolicy: 'max-bundle' });
  pc = connection;
  connection.addTransceiver('video', { direction: 'recvonly' });
  connection.addTransceiver('audio', { direction: 'recvonly' });
  const stream = new MediaStream();
  connection.ontrack = (event) => {
    stream.addTrack(event.track);
    if (video.srcObject !== stream) video.srcObject = stream;
  };
  connection.onconnectionstatechange = () => {
    if (pc !== connection) return;
    const state = connection.connectionState;
    if (state === 'connected') {
      clearTimeout(disconnectTimer);
      failures = 0;
    } else if (state === 'failed') {
      // Duas falhas seguidas costumam ser a rede bloqueando o WebRTC: vale tentar o HLS.
      failures++;
      if (failures >= 2 && tryHls()) return;
      retry('A conexão com a transmissão caiu. Reconectando...', 2000);
    } else if (state === 'disconnected') {
      clearTimeout(disconnectTimer);
      disconnectTimer = setTimeout(() => {
        if (pc === connection && connection.connectionState !== 'connected') retry('A conexão com a transmissão caiu. Reconectando...', 500);
      }, 5000);
    }
  };
  try {
    await connection.setLocalDescription(await connection.createOffer());
    await gathered(connection);
  } catch {
    finish('Este navegador não consegue tocar a transmissão. Tente o Chrome, o Safari ou o Firefox atualizados.');
    return;
  }
  let response;
  try {
    response = await fetch(whepUrl, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: connection.localDescription.sdp });
  } catch {
    retry('Sem conexão com o servidor. Tentando de novo...', 4000);
    return;
  }
  if (pc !== connection) return;
  if (response.status === 404) {
    retry('A transmissão ainda não começou ou está voltando.\nA imagem aparece aqui sozinha.', 3000);
    return;
  }
  if (response.status === 401 || response.status === 403) {
    finish('Esta transmissão terminou, ou o link não está completo.');
    return;
  }
  if (!response.ok) {
    retry(`O servidor respondeu com erro (${response.status}). Tentando de novo...`, 4000);
    return;
  }
  const location = response.headers.get('Location');
  session = location ? new URL(location, whepUrl).href : null;
  try {
    await connection.setRemoteDescription({ type: 'answer', sdp: await response.text() });
  } catch {
    retry('A resposta do servidor não serviu. Tentando de novo...', 4000);
  }
}

// HLS: alguns segundos a mais de atraso, mas passa por qualquer rede que abra páginas.
function tryHls() {
  if (!video.canPlayType('application/vnd.apple.mpegurl')) return false;
  usingHls = true;
  closeSession();
  clearTimeout(retryTimer);
  say('Conectando de outro jeito (com alguns segundos a mais de atraso)...');
  video.srcObject = null;
  video.src = hlsUrl;
  video.play().catch(() => {});
  return true;
}

video.addEventListener('playing', () => {
  say('');
  setLive(true, usingHls ? 'Ponte · ao vivo (HLS)' : 'Ponte · ao vivo');
});
video.addEventListener('error', () => {
  if (usingHls) finish('Não foi possível tocar a transmissão nesta rede.');
});

$('som').onclick = () => {
  video.muted = !video.muted;
  video.play().catch(() => {});
  $('som').textContent = video.muted ? 'Ativar o som' : 'Som ligado';
  $('som').classList.toggle('ligado', !video.muted);
};

// Tela cheia: no app, sem as barras do Android; no navegador, a do próprio navegador. Sair dela não para o vídeo.
let appFullScreen = app ? app.telaCheia() : false;
function showFullScreen() {
  const on = app ? appFullScreen : Boolean(document.fullscreenElement);
  $('tela-cheia').textContent = on ? 'Sair da tela cheia' : 'Tela cheia';
}
$('tela-cheia').onclick = () => {
  if (app) {
    appFullScreen = !appFullScreen;
    app.definirTelaCheia(appFullScreen);
  } else if (document.fullscreenElement) {
    document.exitFullscreen().catch(() => {});
  } else if ($('palco').requestFullscreen) {
    $('palco').requestFullscreen().catch(() => video.webkitEnterFullscreen?.());
  } else {
    video.webkitEnterFullscreen?.();   // iPhone: só o vídeo entra em tela cheia, e sai pelo botão do próprio vídeo
  }
  showFullScreen();
};
document.addEventListener('fullscreenchange', showFullScreen);
showFullScreen();

// Janela flutuante (picture-in-picture): o vídeo numa janelinha por cima dos outros aplicativos.
const floating = $('flutuante');
if (app) {
  if (app.podeFlutuar()) {
    floating.hidden = false;
    floating.onclick = () => app.flutuar();
  }
} else if (document.pictureInPictureEnabled) {
  floating.hidden = false;
  floating.onclick = () => {
    const request = document.pictureInPictureElement ? document.exitPictureInPicture() : video.requestPictureInPicture();
    request.catch(() => {});
  };
} else if (video.webkitSupportsPresentationMode?.('picture-in-picture')) {
  floating.hidden = false;   // Safari (iPhone e iPad)
  floating.onclick = () => video.webkitSetPresentationMode(video.webkitPresentationMode === 'picture-in-picture' ? 'inline' : 'picture-in-picture');
}
// Na janela flutuante do app só cabe o vídeo; ela segue a proporção dele.
window.ponteFlutuante = (on) => document.body.classList.toggle('flutuante', Boolean(on));
function reportSize() {
  if (app?.tamanhoDoVideo && video.videoWidth > 0) app.tamanhoDoVideo(video.videoWidth, video.videoHeight);
}
video.addEventListener('loadedmetadata', reportSize);
video.addEventListener('resize', reportSize);

// Um toque na imagem esconde ou mostra a barra.
video.addEventListener('click', () => $('barra').classList.toggle('escondida'));

window.addEventListener('pagehide', closeSession);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && !ended && !usingHls && (!pc || pc.connectionState !== 'connected')) connect();
});

if (/^[A-Za-z0-9]{24}$/.test(token)) {
  connect();
} else {
  finish('Link incompleto: falta o código da transmissão depois do "#".\nPeça o link de novo para quem está transmitindo.');
}
