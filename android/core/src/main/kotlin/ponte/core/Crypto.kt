package ponte.core

import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.Signature
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** As mesmas contas do programa do Windows (Security.cs), para os dois lados chegarem aos mesmos valores. */
object Crypto {
    fun sha256(vararg parts: ByteArray): ByteArray {
        val digest = MessageDigest.getInstance("SHA-256")
        for (p in parts) digest.update(p)
        return digest.digest()
    }

    fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02X".format(it) }

    /** ID de 9 dígitos derivado da chave pública (formato SPKI). */
    fun idFromSpki(spki: ByteArray): String {
        val hash = sha256(spki)
        var value = 0UL
        for (i in 0 until 8) value = (value shl 8) or (hash[i].toULong() and 0xFFUL)
        return (100_000_000UL + value % 900_000_000UL).toString()
    }

    fun formatId(id: String): String =
        if (id.length == 9) "${id.substring(0, 3)} ${id.substring(3, 6)} ${id.substring(6)}" else id

    private fun hmacSha256(key: ByteArray): Mac =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }

    /** PBKDF2-HMAC-SHA256 com saída de 32 bytes; a senha entra em UTF-8, como no .NET. */
    fun pbkdf2(password: String, salt: ByteArray, iterations: Int): ByteArray {
        val mac = hmacSha256(password.toByteArray(Charsets.UTF_8))
        mac.update(salt)
        mac.update(byteArrayOf(0, 0, 0, 1))
        var block = mac.doFinal()
        val result = block.copyOf()
        for (i in 1 until iterations) {
            block = mac.doFinal(block)
            for (j in result.indices) result[j] = (result[j].toInt() xor block[j].toInt()).toByte()
        }
        return result
    }

    /** Prova de que quem conecta conhece a senha, amarrada ao certificado que ele viu do outro lado. */
    fun proof(verifier: ByteArray, nonce: ByteArray, certificateHash: ByteArray): ByteArray =
        hmacSha256(verifier).run {
            update(nonce)
            doFinal(certificateHash)
        }

    /** O que o visualizador assina para provar que é dono da própria chave. */
    fun pairingMessage(nonce: ByteArray, certificateHash: ByteArray): ByteArray =
        "ponte-autorizacao\n".toByteArray(Charsets.US_ASCII) + nonce + certificateHash

    /**
     * O Java entrega a assinatura ECDSA em DER (SEQUENCE de dois INTEGER); o .NET espera r e s lado a
     * lado, cada um com [size] bytes.
     */
    fun derToP1363(der: ByteArray, size: Int = 32): ByteArray {
        var pos = 0
        fun byte(): Int = der.getOrElse(pos++) { throw IllegalArgumentException("Assinatura DER truncada.") }.toInt() and 0xFF
        fun length(): Int {
            val first = byte()
            if (first < 0x80) return first
            var n = 0
            repeat(first and 0x7F) { n = (n shl 8) or byte() }
            return n
        }
        require(byte() == 0x30) { "Assinatura DER sem SEQUENCE." }
        length()
        val out = ByteArray(size * 2)
        for (part in 0 until 2) {
            require(byte() == 0x02) { "Assinatura DER sem INTEGER." }
            var len = length()
            require(pos + len <= der.size) { "Assinatura DER truncada." }
            while (len > size && der[pos] == 0.toByte()) { pos++; len-- }
            require(len <= size) { "Inteiro grande demais na assinatura." }
            System.arraycopy(der, pos, out, part * size + (size - len), len)
            pos += len
        }
        return out
    }
}

/** A identidade de quem conecta: a chave pública que o outro lado guarda e a capacidade de assinar com a privada. */
interface Identity {
    /** Chave pública em formato SPKI (DER). */
    val spki: ByteArray

    /** Assinatura ECDSA P-256 com SHA-256, no formato r||s que o Ponte do Windows confere. */
    fun sign(data: ByteArray): ByteArray
}

/** Serve tanto para uma chave em memória (testes) quanto para uma guardada no cofre do Android. */
class EcIdentity(private val privateKey: PrivateKey, publicKey: PublicKey) : Identity {
    override val spki: ByteArray = publicKey.encoded

    override fun sign(data: ByteArray): ByteArray {
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(privateKey)
        signature.update(data)
        return Crypto.derToP1363(signature.sign())
    }
}
