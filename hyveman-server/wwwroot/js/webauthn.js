// WebAuthn ceremony helpers for the auth pages. Converts ArrayBuffers ↔ base64url.

window.hyveman = {
    b64url: {
        encode(buf) {
            const bytes = new Uint8Array(buf);
            let s = '';
            for (let i = 0; i < bytes.length; i += 0x8000) {
                s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            }
            return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        },
        decode(str) {
            const t = str.replace(/-/g, '+').replace(/_/g, '/');
            const pad = t.length % 4 === 2 ? '==' : t.length % 4 === 3 ? '=' : '';
            const bin = atob(t + pad);
            const bytes = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
            return bytes;
        }
    },

    // optionsJson: the server's credential creation options (challenge etc. base64url).
    // Returns the attestation response with base64url fields.
    async createCredential(optionsJson) {
        const o = JSON.parse(optionsJson);
        const publicKey = {
            challenge: this.b64url.decode(o.challenge),
            rp: o.rp,
            user: { id: this.b64url.decode(o.user.id), name: o.user.name, displayName: o.user.displayName },
            pubKeyCredParams: o.pubKeyCredParams.map(p => ({ type: p.type, alg: p.alg })),
            timeout: o.timeout,
            attestation: o.attestation || 'none',
            excludeCredentials: (o.excludeCredentials || []).map(c => ({ id: this.b64url.decode(c.id), type: c.type })),
        };
        if (o.authenticatorSelection) publicKey.authenticatorSelection = o.authenticatorSelection;
        if (o.extensions) publicKey.extensions = o.extensions;

        const cred = await navigator.credentials.create({ publicKey });
        const r = cred.response;
        return {
            id: cred.id,
            rawId: this.b64url.encode(cred.rawId),
            type: cred.type,
            response: {
                clientDataJSON: this.b64url.encode(r.clientDataJSON),
                attestationObject: this.b64url.encode(r.attestationObject),
                transports: r.getTransports ? r.getTransports() : []
            }
        };
    },

    // optionsJson: assertion options. Returns the assertion response (base64url fields).
    async getCredential(optionsJson) {
        const o = JSON.parse(optionsJson);
        const publicKey = {
            challenge: this.b64url.decode(o.challenge),
            timeout: o.timeout,
            userVerification: o.userVerification || 'preferred',
        };
        if (o.rpId) publicKey.rpId = o.rpId;
        if (o.allowCredentials && o.allowCredentials.length) {
            publicKey.allowCredentials = o.allowCredentials.map(c => ({ id: this.b64url.decode(c.id), type: c.type || 'public-key' }));
        }
        if (o.extensions) publicKey.extensions = o.extensions;

        const cred = await navigator.credentials.get({ publicKey });
        const r = cred.response;
        return {
            id: cred.id,
            rawId: this.b64url.encode(cred.rawId),
            type: cred.type,
            response: {
                clientDataJSON: this.b64url.encode(r.clientDataJSON),
                authenticatorData: this.b64url.encode(r.authenticatorData),
                signature: this.b64url.encode(r.signature),
                userHandle: r.userHandle ? this.b64url.encode(r.userHandle) : null
            }
        };
    }
};
