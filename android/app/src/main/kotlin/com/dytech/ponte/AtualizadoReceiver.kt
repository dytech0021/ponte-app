package com.dytech.ponte

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * Depois que a atualização pedida no próprio Ponte termina, o Android avisa a versão nova (MY_PACKAGE_REPLACED). Ele
 * não deixa um aplicativo abrir uma tela sozinho em segundo plano (nem pela resposta do instalador, nem por alarme:
 * testado no Android 15), então o Ponte avisa por notificação: "Ponte atualizado, toque para abrir".
 */
class AtualizadoReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        // Só quando a atualização foi pedida no aplicativo há pouco (e não, por exemplo, instalada à mão).
        val prefs = context.getSharedPreferences("ponte", Context.MODE_PRIVATE)
        val asked = prefs.getLong(UpdateFlow.REOPEN_KEY, 0L)
        prefs.edit().remove(UpdateFlow.REOPEN_KEY).apply()
        if (System.currentTimeMillis() - asked > 10 * 60 * 1000L) return
        notify(context)
    }

    companion object {
        const val NOTIFICATION_ID = 7412
        private const val CHANNEL = "atualizacao"

        fun notify(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            // Importância alta: aparece por cima da tela, no alto, como uma mensagem.
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Atualizações do Ponte", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Avisa quando o Ponte terminou de se atualizar."
            })
            val open = Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
                .putExtra(MainActivity.EXTRA_UPDATED, true)
            val notification = Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_notificacao)
                .setContentTitle("Ponte atualizado")
                .setContentText("Versão ${BuildConfig.VERSION_NAME} instalada. Toque para abrir.")
                .setContentIntent(PendingIntent.getActivity(context, 0, open, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
                .setAutoCancel(true)
                .setTimeoutAfter(30 * 60 * 1000L)
                .setCategory(Notification.CATEGORY_STATUS)
                .build()
            // Sem a permissão (recusada), o Android descarta: fica para quem tocar no ícone.
            if (manager.areNotificationsEnabled()) manager.notify(NOTIFICATION_ID, notification)
        }
    }
}
