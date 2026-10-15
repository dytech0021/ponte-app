package ponte.core

import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.concurrent.thread

/**
 * Quais computadores salvos estão com o Ponte aberto agora, para a bolinha verde ou vermelha da lista. Os salvos
 * por ID perguntam ao servidor de retransmissão (é onde eles ficam registrados enquanto o Ponte está aberto, com um
 * sinal a cada 25 s); os salvos por IP, testando a porta direta. Bloqueante: use fora da thread da tela.
 */
object Presence {
    /** Por chave ("id:..." ou "ip:..."): true online, false offline. O que não deu para saber fica de fora. */
    fun check(relayServer: String, relayKey: String, targets: List<Target>, probeMillis: Int = 3000): Map<String, Boolean> {
        val result = HashMap<String, Boolean>()
        val probes = targets.filter { !it.isId }.distinctBy { it.key }.map { target ->
            thread(isDaemon = true, name = "ponte-online") {
                val online = probe(target.host, target.port, probeMillis)
                synchronized(result) { result[target.key] = online }
            }
        }
        val ids = targets.filter { it.isId }.map { it.id }.distinct()
        if (ids.isNotEmpty() && relayServer.isNotBlank()) {
            askRelay(relayServer, relayKey, ids)?.let { online -> synchronized(result) { online.forEach { (id, on) -> result["id:$id"] = on } } }
        }
        probes.forEach { it.join() }
        return synchronized(result) { HashMap(result) }
    }

    // Servidor fora do ar, ou ainda sem o STATUS: devolve null e a lista fica sem bolinha, em vez de tudo vermelho.
    private fun askRelay(server: String, key: String, ids: List<String>): Map<String, Boolean>? {
        val clean = key.filter { it.isLetterOrDigit() && it.code < 128 || it == '-' || it == '_' }
        val (host, port) = Target.endpoint(server, Target.RELAY_PORT)
        val online = HashMap<String, Boolean>()
        try {
            for (chunk in ids.chunked(100)) {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(host, port), 10_000)
                    socket.soTimeout = 10_000
                    val line = "PONTE STATUS ${chunk.joinToString(",")}${if (clean.isEmpty()) "" else " $clean"}\n"
                    socket.getOutputStream().apply {
                        write(line.toByteArray(Charsets.US_ASCII))
                        flush()
                    }
                    val reply = readLine(socket)
                    if (!reply.startsWith("STATUS ") || reply.length != 7 + chunk.size) return null
                    chunk.forEachIndexed { i, id -> online[id] = reply[7 + i] == '1' }
                }
            }
            return online
        } catch (e: IOException) {
            return null
        } catch (e: IllegalArgumentException) {
            return null
        }
    }

    private fun readLine(socket: Socket): String {
        val input = socket.getInputStream()
        val line = StringBuilder()
        while (line.length < 256) {
            val b = input.read()
            if (b < 0 || b == '\n'.code) break
            if (b != '\r'.code) line.append(b.toChar())
        }
        return line.toString()
    }

    // Por IP: o Ponte aberto lá aceita conexão na porta direta. Só abre e fecha, sem identificação nenhuma.
    private fun probe(host: String, port: Int, millis: Int): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(host, port), millis) }
        true
    } catch (e: IOException) {
        false
    } catch (e: IllegalArgumentException) {
        false
    }
}
