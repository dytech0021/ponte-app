package com.dytech.ponte

import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import java.io.File
import kotlin.concurrent.thread
import ponte.core.AndroidUpdate
import ponte.core.UpdateFeed

/**
 * Atualização do aplicativo pelo servidor de retransmissão: procura, baixa, confere e entrega ao
 * instalador do Android, que pede a confirmação de quem usa. Não há instalação silenciosa.
 */
class UpdateFlow(private val activity: Activity, private val store: Store) {
    private var busy = false
    // Voltando da tela do Android que libera a instalação, a atualização já baixada segue sozinha.
    private var waitingPermission = false

    /** Ao abrir o aplicativo (e na volta à tela inicial): confere sozinho, no máximo uma vez por minuto. */
    fun onResume() {
        if (waitingPermission && canInstall()) {
            waitingPermission = false
            install()
            return
        }
        if (!automatic || store.relayServer.isBlank()) return
        if (System.currentTimeMillis() - store.lastUpdateCheck < CHECK_INTERVAL_MS) return
        check(manual = false)
    }

    /** Procura uma versão nova. [manual] avisa também quando não há nada ou quando deu errado. */
    fun check(manual: Boolean) {
        if (busy) return
        busy = true
        store.lastUpdateCheck = System.currentTimeMillis()
        val feed = UpdateFeed(store.relayServer, store.relayKey)
        thread(name = "ponte-atualizacao") {
            val result = runCatching { feed.check(BuildConfig.VERSION_CODE) }
            activity.runOnUiThread {
                busy = false
                if (activity.isFinishing || activity.isDestroyed) return@runOnUiThread
                val update = result.getOrNull()
                when {
                    update != null -> download(feed, update)
                    !manual -> {}
                    result.isFailure -> tell("Não foi possível verificar a atualização: ${result.exceptionOrNull()?.message ?: "erro desconhecido"}.")
                    else -> {
                        UpdateProvider.file(activity).delete()
                        tell("Você já está com a versão mais recente (${BuildConfig.VERSION_NAME}).")
                    }
                }
            }
        }
    }

    // A versão nova é baixada sem perguntar; a única confirmação é a do instalador do Android, que não
    // dá para dispensar. Um download já feito (de uma vez em que a instalação foi recusada) é reaproveitado.
    private fun download(feed: UpdateFeed, update: AndroidUpdate) {
        busy = true
        var abandoned = false
        val waiting = AlertDialog.Builder(activity)
            .setTitle("Atualizando o Ponte")
            .setMessage("Baixando a versão ${update.versionName} (esta é a ${BuildConfig.VERSION_NAME}). A lista de computadores e as autorizações continuam.")
            .setCancelable(false)
            .setNegativeButton("Agora não") { _, _ -> abandoned = true }
            .show()
        val apk = UpdateProvider.file(activity)
        thread(name = "ponte-download") {
            val problem = try {
                val ready = apk.isFile && update.matches(apk.readBytes())
                if (!ready) {
                    apk.parentFile?.mkdirs()
                    feed.download(update, apk)
                }
                verify(apk, update)
            } catch (e: Exception) {
                apk.delete()
                "O download da atualização não terminou: ${e.message ?: e.javaClass.simpleName}."
            }
            activity.runOnUiThread {
                busy = false
                if (waiting.isShowing) waiting.dismiss()
                if (abandoned || activity.isFinishing || activity.isDestroyed) return@runOnUiThread
                if (problem != null) tell(problem) else install()
            }
        }
    }

    // O arquivo tem de ser o Ponte, na versão anunciada, assinado com o mesmo certificado do instalado.
    private fun verify(apk: File, update: AndroidUpdate): String? {
        val manager = activity.packageManager
        val candidate = manager.getPackageArchiveInfo(apk.path, UpdatePackage.FLAGS)
        val installed = manager.getPackageInfo(activity.packageName, UpdatePackage.FLAGS)
        if (candidate == null || UpdatePackage.code(candidate) != update.versionCode.toLong() || !UpdatePackage.compatible(installed, candidate)) {
            apk.delete()
            return "O arquivo recebido não é uma versão mais nova do Ponte assinada com a mesma chave deste aplicativo, e foi descartado."
        }
        return null
    }

    private fun canInstall() = activity.packageManager.canRequestPackageInstalls()

    private fun install() {
        if (!UpdateProvider.file(activity).isFile) return
        if (!canInstall()) {
            AlertDialog.Builder(activity)
                .setTitle("Permitir a instalação")
                .setMessage("Para se atualizar, o Ponte precisa da permissão para instalar aplicativos. Na tela que vai abrir, ligue \"Permitir desta fonte\" e volte ao Ponte.")
                .setPositiveButton("Abrir") { _, _ ->
                    waitingPermission = true
                    activity.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${activity.packageName}")))
                }
                .setNegativeButton("Cancelar", null)
                .show()
            return
        }
        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(UpdateProvider.uri(activity), UpdateProvider.APK_TYPE)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try {
            activity.startActivity(intent)
        } catch (e: ActivityNotFoundException) {
            tell("Este aparelho não abriu o instalador de aplicativos.")
        }
    }

    private fun tell(message: String) {
        AlertDialog.Builder(activity).setMessage(message).setPositiveButton("Fechar", null).show()
    }

    companion object {
        private const val CHECK_INTERVAL_MS = 60 * 1000L

        /** Os testes desligam a consulta automática, para não falar com o servidor de verdade. */
        @Volatile internal var automatic = true
    }
}
