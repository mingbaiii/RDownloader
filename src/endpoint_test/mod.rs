pub mod connectivity_test;
pub mod endpoint;
pub mod speed_test;

pub use connectivity_test::{
    format_results, test_connectivity, test_connectivity_dns, test_ip, ConnectivityResult,
    PingResult, TcpPingResult, TestConfig,
};

pub use endpoint::{DataEndpoint, EndpointList};

pub use speed_test::{
    format_speed_results, test_speed, test_speed_list, SpeedResult, SpeedTestConfig,
};
