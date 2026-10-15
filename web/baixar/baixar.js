// Ponte · baixar o aplicativo para assistir a uma transmissão. Chega aqui quem tocou em "Abrir no app Ponte" sem ter
// o app. O APK só sai com o código de uma transmissão no ar (o servidor confere), e não para qualquer um.

const $ = (id) => document.getElementById(id);
const token = decodeURIComponent(location.hash.slice(1)).trim();
const valid = /^[A-Za-z0-9]{24}$/.test(token);
const apk = `Ponte.apk?t=${token}`;
const android = /Android/i.test(navigator.userAgent);

$('navegador').href = new URL(`../assistir/#${token}`, location.href).href;
$('baixar').href = apk;
// Com o app instalado, o Android abre direto nele (o botão da página de assistir usa o mesmo caminho).
$('abrir').href = `intent://assistir/${token}#Intent;scheme=ponte;package=com.dytech.ponte;end`;

function problem(text) {
  $('estado').textContent = text;
  $('estado').hidden = false;
  $('baixar').hidden = true;
  $('abrir').hidden = true;
  document.querySelector('.passos').hidden = true;
}

if (!android) {
  $('android').hidden = true;
  $('outro').textContent = /iPhone|iPad|iPod/.test(navigator.userAgent)
    ? 'O aplicativo do Ponte é para Android. No iPhone, assista pelo navegador.'
    : 'O aplicativo para celular é para Android. Neste aparelho, assista pelo navegador ou pelo Ponte do computador.';
  $('outro').hidden = false;
} else if (!valid) {
  problem('Link incompleto: falta o código da transmissão depois do "#". Peça o link de novo para quem está transmitindo.');
} else {
  // Transmissão já encerrada: o servidor não entrega o app por este link.
  fetch(apk, { method: 'HEAD' }).then((response) => {
    if (response.status === 403) {
      problem('A transmissão deste link já terminou, então o download por aqui não está disponível. Peça o aplicativo ' +
        'para quem transmite: no Ponte do computador, "Aplicativo para celular".');
    }
  }).catch(() => {});
}
