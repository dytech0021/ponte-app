package com.dytech.ponte

import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.content.pm.Signature
import android.os.Build

/**
 * Confere um APK baixado antes de oferecer a instalação. O manifesto vem pelo mesmo servidor que o
 * APK e não prova nada; quem decide é o próprio arquivo: o pacote tem de ser o do Ponte, a versão
 * tem de ser mais nova e o certificado que o assina tem de ser o do aplicativo instalado.
 */
object UpdatePackage {
    /** O que pedir ao Android para ler a versão e os certificados de um pacote. */
    @Suppress("DEPRECATION")
    val FLAGS: Int =
        if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES or PackageManager.GET_SIGNATURES
        else PackageManager.GET_SIGNATURES

    fun compatible(installed: PackageInfo, candidate: PackageInfo): Boolean {
        if (candidate.packageName != installed.packageName) return false
        if (code(candidate) <= code(installed)) return false
        val mine = signers(installed) ?: return false
        val theirs = signers(candidate) ?: return false
        return mine == theirs
    }

    @Suppress("DEPRECATION")
    fun code(info: PackageInfo): Long = if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else info.versionCode.toLong()

    @Suppress("DEPRECATION")
    private fun signers(info: PackageInfo): Set<Signature>? {
        if (Build.VERSION.SDK_INT >= 28) {
            info.signingInfo?.let { signing ->
                val list = if (signing.hasMultipleSigners()) signing.apkContentsSigners else signing.signingCertificateHistory
                if (!list.isNullOrEmpty()) return list.toSet()
            }
        }
        return info.signatures?.takeIf { it.isNotEmpty() }?.toSet()
    }
}
