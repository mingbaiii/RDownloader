use std::net::{Ipv4Addr, Ipv6Addr};

/// DNS 记录类型
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecordType {
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
    AAAA = 28,
}

impl RecordType {
    pub fn from_u16(value: u16) -> Option<Self> {
        match value {
            1 => Some(RecordType::A),
            2 => Some(RecordType::NS),
            5 => Some(RecordType::CNAME),
            6 => Some(RecordType::SOA),
            28 => Some(RecordType::AAAA),
            _ => None,
        }
    }
}

/// DNS 记录
#[derive(Debug, Clone)]
pub enum DnsRecord {
    A {
        name: String,
        ttl: u32,
        addr: Ipv4Addr,
    },
    AAAA {
        name: String,
        ttl: u32,
        addr: Ipv6Addr,
    },
    CNAME {
        name: String,
        ttl: u32,
        target: String,
    },
}

/// DNS 消息
#[derive(Debug)]
pub struct DnsMessage {
    pub id: u16,
    pub questions: Vec<Question>,
    pub answers: Vec<DnsRecord>,
}

/// DNS 问题
#[derive(Debug)]
pub struct Question {
    pub name: String,
    pub record_type: RecordType,
}

/// 构造 DNS 查询消息
pub fn build_query(domain: &str, record_type: RecordType) -> Vec<u8> {
    let id = rand::random::<u16>();
    let mut buf = Vec::new();

    // Header
    buf.extend_from_slice(&id.to_be_bytes());
    buf.extend_from_slice(&0x0100u16.to_be_bytes()); // Flags: standard query, recursion desired
    buf.extend_from_slice(&1u16.to_be_bytes()); // QDCOUNT: 1
    buf.extend_from_slice(&0u16.to_be_bytes()); // ANCOUNT: 0
    buf.extend_from_slice(&0u16.to_be_bytes()); // NSCOUNT: 0
    buf.extend_from_slice(&0u16.to_be_bytes()); // ARCOUNT: 0

    // Question
    encode_domain_name(&mut buf, domain);
    buf.extend_from_slice(&(record_type as u16).to_be_bytes());
    buf.extend_from_slice(&1u16.to_be_bytes()); // QCLASS: IN

    buf
}

/// 解析 DNS 响应消息
pub fn parse_response(data: &[u8]) -> Result<DnsMessage, String> {
    if data.len() < 12 {
        return Err("响应太短".to_string());
    }

    let id = u16::from_be_bytes([data[0], data[1]]);
    let qdcount = u16::from_be_bytes([data[4], data[5]]) as usize;
    let ancount = u16::from_be_bytes([data[6], data[7]]) as usize;

    let mut offset = 12;

    // 解析问题部分
    let mut questions = Vec::new();
    for _ in 0..qdcount {
        let (name, new_offset) = decode_domain_name(data, offset)?;
        offset = new_offset;
        if offset + 4 > data.len() {
            return Err("问题部分太短".to_string());
        }
        let qtype = u16::from_be_bytes([data[offset], data[offset + 1]]);
        offset += 4; // Skip QTYPE and QCLASS
        questions.push(Question {
            name,
            record_type: RecordType::from_u16(qtype).unwrap_or(RecordType::A),
        });
    }

    // 解析回答部分
    let mut answers = Vec::new();
    for _ in 0..ancount {
        let (name, new_offset) = decode_domain_name(data, offset)?;
        offset = new_offset;

        if offset + 10 > data.len() {
            return Err("回答部分太短".to_string());
        }

        let rtype = u16::from_be_bytes([data[offset], data[offset + 1]]);
        offset += 2;
        offset += 2; // Skip CLASS
        let ttl = u32::from_be_bytes([
            data[offset],
            data[offset + 1],
            data[offset + 2],
            data[offset + 3],
        ]);
        offset += 4;
        let rdlength = u16::from_be_bytes([data[offset], data[offset + 1]]) as usize;
        offset += 2;

        if offset + rdlength > data.len() {
            return Err("RDATA 部分太短".to_string());
        }

        match rtype {
            1 => {
                // A record
                if rdlength == 4 {
                    let addr = Ipv4Addr::new(
                        data[offset],
                        data[offset + 1],
                        data[offset + 2],
                        data[offset + 3],
                    );
                    answers.push(DnsRecord::A {
                        name,
                        ttl,
                        addr,
                    });
                }
            }
            28 => {
                // AAAA record
                if rdlength == 16 {
                    let addr = Ipv6Addr::new(
                        u16::from_be_bytes([data[offset], data[offset + 1]]),
                        u16::from_be_bytes([data[offset + 2], data[offset + 3]]),
                        u16::from_be_bytes([data[offset + 4], data[offset + 5]]),
                        u16::from_be_bytes([data[offset + 6], data[offset + 7]]),
                        u16::from_be_bytes([data[offset + 8], data[offset + 9]]),
                        u16::from_be_bytes([data[offset + 10], data[offset + 11]]),
                        u16::from_be_bytes([data[offset + 12], data[offset + 13]]),
                        u16::from_be_bytes([data[offset + 14], data[offset + 15]]),
                    );
                    answers.push(DnsRecord::AAAA {
                        name,
                        ttl,
                        addr,
                    });
                }
            }
            5 => {
                // CNAME record
                let (target, _) = decode_domain_name(data, offset)?;
                answers.push(DnsRecord::CNAME {
                    name,
                    ttl,
                    target,
                });
            }
            _ => {
                // 其他记录类型，跳过
            }
        }

        offset += rdlength;
    }

    Ok(DnsMessage {
        id,
        questions,
        answers,
    })
}

/// 编码域名到 DNS 格式
fn encode_domain_name(buf: &mut Vec<u8>, domain: &str) {
    for part in domain.split('.') {
        buf.push(part.len() as u8);
        buf.extend_from_slice(part.as_bytes());
    }
    buf.push(0); // 结束标记
}

/// 从 DNS 格式解码域名
fn decode_domain_name(data: &[u8], mut offset: usize) -> Result<(String, usize), String> {
    let mut parts = Vec::new();
    let mut jumped = false;
    let mut original_offset = 0;

    loop {
        if offset >= data.len() {
            return Err("域名解析超出范围".to_string());
        }

        let len = data[offset] as usize;

        if len == 0 {
            if !jumped {
                offset += 1;
            }
            break;
        }

        // 压缩指针
        if len & 0xC0 == 0xC0 {
            if offset + 1 >= data.len() {
                return Err("压缩指针超出范围".to_string());
            }
            if !jumped {
                original_offset = offset + 2;
            }
            let pointer =
                u16::from_be_bytes([data[offset] & 0x3F, data[offset + 1]]) as usize;
            offset = pointer;
            jumped = true;
            continue;
        }

        offset += 1;
        if offset + len > data.len() {
            return Err("域名标签超出范围".to_string());
        }

        let part = String::from_utf8_lossy(&data[offset..offset + len]).to_string();
        parts.push(part);
        offset += len;
    }

    let name = parts.join(".");
    let next_offset = if jumped { original_offset } else { offset };

    Ok((name, next_offset))
}
