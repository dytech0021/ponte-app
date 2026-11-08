package com.dytech.ponte

import android.content.Context
import ponte.core.HostCrypto
import ponte.core.NotReleasedException
import ponte.core.PonteException
import ponte.core.Ticket

/**
 * Conta obrigatória (1.17.0): o Ponte só funciona com uma conta liberada pelo dono. O servidor do Ponte confere a conta
 * e assina um bilhete preso a uma chave. Este aparelho tem duas: a de quem acessa (no cofre do Android) e a do celular
 * como acessado. Cada uma ganha o seu bilhete, que vale 14 dias e é renovado quando falta menos de uma semana.
 */
object BilheteConta {
    private fun now() = System.currentTimeMillis() / 1000

    /** Os dois bilhetes existem e não venceram (a assinatura e a chave são conferidas na hora da conexão). */
    fun ok(store: Store): Boolean = Ticket.expires(store.ticket) > now() && Ticket.expires(store.hostTicket) > now()

    /** O bilhete do celular como acessado está em dia. */
    fun hostOk(store: Store): Boolean = Ticket.expires(store.hostTicket) > now()

    /** Falta menos de uma semana para algum dos dois vencer. */
    fun needsRenewal(store: Store): Boolean =
        minOf(Ticket.expires(store.ticket), Ticket.expires(store.hostTicket)) - now() < Ticket.RENEW_BEFORE_SECONDS

    /** Pede os dois bilhetes ao servidor e guarda. Bloqueia: chame fora da thread da tela. */
    @Throws(AccountException::class, PonteException::class, NotReleasedException::class)
    fun renew(context: Context, store: Store, account: Account) {
        val token = account.accessToken()
        val viewer = Ticket.request(store.relayServer, store.relayKey, HostCrypto.keyHash(DeviceIdentity.get().spki), token)
        val host = Ticket.request(store.relayServer, store.relayKey, HostCrypto.keyHash(AcessoIdentity.get(context).spki), token)
        store.ticket = viewer
        store.hostTicket = host
    }
}
