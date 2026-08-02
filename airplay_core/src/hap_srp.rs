//! HomePod transient 配对使用的 SRP-6a 3072/SHA-512 客户端。
//! 这里按 HomeKit 要求对 k、u 输入补齐到 384 字节，并使用完整的客户端证明公式。
use getrandom::getrandom;
use num_bigint::BigUint;
use sha2::{Digest, Sha512};
use srp::{client::SrpClient, groups::G_3072};

use crate::error::{CoreError, Result};

const GROUP_BYTES: usize = 384;
const USERNAME: &[u8] = b"Pair-Setup";

pub struct HapSrpClient {
    public_a: Vec<u8>,
    proof_m1: Vec<u8>,
    expected_m2: Vec<u8>,
    session_key: Vec<u8>,
}

impl HapSrpClient {
    pub fn start(salt: &[u8], server_b: &[u8], password: &str) -> Result<Self> {
        if salt.is_empty() || server_b.is_empty() {
            return Err(CoreError::InvalidArgument);
        }
        let mut secret_bytes = [0_u8; 32];
        getrandom(&mut secret_bytes).map_err(|_| CoreError::Authentication)?;
        let secret_a = BigUint::from_bytes_be(&secret_bytes);
        let client = SrpClient::<Sha512>::new(&G_3072);
        let public_a_num = client.compute_a_pub(&secret_a);
        let server_b_num = BigUint::from_bytes_be(server_b);
        if &server_b_num % &G_3072.n == BigUint::default() {
            return Err(CoreError::Authentication);
        }

        let k = BigUint::from_bytes_be(&hash_padded_pair(&G_3072.n, &G_3072.g));
        let u = BigUint::from_bytes_be(&hash_padded_pair(&public_a_num, &server_b_num));
        let mut identity = Sha512::new();
        identity.update(USERNAME);
        identity.update(b":");
        identity.update(password.as_bytes());
        let mut x_hash = Sha512::new();
        x_hash.update(salt);
        x_hash.update(identity.finalize());
        let x = BigUint::from_bytes_be(&x_hash.finalize());
        let premaster = client.compute_premaster_secret(&server_b_num, &k, &x, &secret_a, &u);
        let session_key = Sha512::digest(premaster.to_bytes_be()).to_vec();
        let public_a = public_a_num.to_bytes_be();
        let natural_b = server_b_num.to_bytes_be();
        let proof_m1 = client_proof(salt, &public_a, &natural_b, &session_key);
        let mut server_proof = Sha512::new();
        server_proof.update(&public_a);
        server_proof.update(&proof_m1);
        server_proof.update(&session_key);
        Ok(Self {
            public_a,
            proof_m1,
            expected_m2: server_proof.finalize().to_vec(),
            session_key,
        })
    }

    pub fn public_a(&self) -> &[u8] {
        &self.public_a
    }
    pub fn proof_m1(&self) -> &[u8] {
        &self.proof_m1
    }
    pub fn session_key(&self) -> &[u8] {
        &self.session_key
    }
    pub fn verify_server_proof(&self, proof: &[u8]) -> Result<()> {
        if proof.len() != self.expected_m2.len() {
            return Err(CoreError::Authentication);
        }
        let difference = proof
            .iter()
            .zip(&self.expected_m2)
            .fold(0_u8, |acc, (a, b)| acc | (a ^ b));
        if difference == 0 {
            Ok(())
        } else {
            Err(CoreError::Protocol("SRP 服务器证明不匹配"))
        }
    }
}

fn pad(value: &BigUint) -> Vec<u8> {
    let bytes = value.to_bytes_be();
    let mut result = vec![0; GROUP_BYTES.saturating_sub(bytes.len())];
    result.extend_from_slice(&bytes);
    result
}
fn hash_padded_pair(left: &BigUint, right: &BigUint) -> Vec<u8> {
    let mut hash = Sha512::new();
    hash.update(pad(left));
    hash.update(pad(right));
    hash.finalize().to_vec()
}
fn client_proof(salt: &[u8], public_a: &[u8], server_b: &[u8], key: &[u8]) -> Vec<u8> {
    let hn = Sha512::digest(G_3072.n.to_bytes_be());
    let hg = Sha512::digest(G_3072.g.to_bytes_be());
    let xor: Vec<u8> = hn.iter().zip(hg).map(|(a, b)| a ^ b).collect();
    let mut hash = Sha512::new();
    hash.update(xor);
    hash.update(Sha512::digest(USERNAME));
    hash.update(salt);
    hash.update(public_a);
    hash.update(server_b);
    hash.update(key);
    hash.finalize().to_vec()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn padded_group_values_are_384_bytes() {
        assert_eq!(pad(&G_3072.n).len(), GROUP_BYTES);
        assert_eq!(pad(&G_3072.g).len(), GROUP_BYTES);
    }
    #[test]
    fn bad_server_public_key_is_rejected() {
        assert!(HapSrpClient::start(&[1; 16], &[0], "3939").is_err());
    }
}
