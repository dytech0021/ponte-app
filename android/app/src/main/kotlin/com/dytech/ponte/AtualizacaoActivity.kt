package com.dytech.ponte

import android.app.Activity
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.os.Bundle
import android.widget.Toast

/**
 * A resposta do Android à instalação da atualização (sem tela própria). Se ele pedir a confirmação de quem usa,
 * mostra a tela dele; instalada a versão nova, abre o Ponte de novo, já nela; deu errado, avisa.
 */
class AtualizacaoActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        handle(intent)
        finish()
    }

    private fun handle(intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                val confirm = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                    else @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_INTENT)
                if (confirm == null) {
                    failed("O Android não mostrou a confirmação da instalação.")
                    return
                }
                try { startActivity(confirm) } catch (e: RuntimeException) { failed("O Android não mostrou a confirmação da instalação.") }
            }
            PackageInstaller.STATUS_SUCCESS -> {
                UpdateProvider.file(this).delete()
                startActivity(Intent(this, MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
                    .putExtra(MainActivity.EXTRA_UPDATED, true))
            }
            PackageInstaller.STATUS_FAILURE_ABORTED -> failed(null)
            else -> failed(intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE))
        }
    }

    private fun failed(message: String?) {
        UpdateFlow.State.installing = false
        UpdateFlow.State.changed()
        Toast.makeText(this, if (message == null) "Atualização cancelada." else "A atualização não foi instalada: $message", Toast.LENGTH_LONG).show()
    }
}
