pub mod dns_client;
pub mod dns_message;
pub mod doh_dns_client;
pub mod mix_dns_client;
pub mod system_dns_client;
pub mod udp_dns_client;

pub use dns_client::{
    create_dns_client, create_dns_client_from_str, create_dns_client_with_config,
    create_mix_dns_client, create_mix_dns_client_with_config, create_mix_dns_client_with_proxy,
    create_mix_dns_client_with_proxy_and_config,
    create_system_dns_client, create_system_dns_client_with_config, DnsClient, DnsConfig,
    DnsError, DnsResult, DnsServerConfig,
};
