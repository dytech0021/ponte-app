// Leva o ?code= (ou o erro, que pode vir depois do #) do login com Google de volta ao aplicativo de iPhone.
const back = `ponte://conta${location.search}${location.hash}`;
document.getElementById('voltar').href = back;
location.replace(back);
